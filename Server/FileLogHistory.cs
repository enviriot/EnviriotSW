///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace X13 {
  /// <summary>Готовое сообщение с местным временем сервера; Id отсутствует без подтверждённой файловой записи.</summary>
  public sealed class LogEntry {
    public string Id { get; private set; }
    public DateTime Timestamp { get; private set; }
    public LogLevel Level { get; private set; }
    public string Message { get; private set; }

    internal LogEntry(string id, DateTime timestamp, LogLevel level, string message) {
      Id = id;
      Timestamp = new DateTime(timestamp.Ticks - timestamp.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Unspecified);
      Level = level;
      Message = (message ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
    }
  }

  /// <summary>Суточные файлы журнала v2 и чтение назад по идентификатору записи.</summary>
  internal sealed class FileLogHistory {
    internal const int MaxCount = 200;
    internal const int KeepDays = 36;
    private const string HeaderPrefix = "# EnviriotLog v2 date=";
    private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
    private readonly string _directory;
    private readonly object _sync = new object();

    internal FileLogHistory(string directory) {
      _directory = Path.GetFullPath(directory);
      Directory.CreateDirectory(_directory);
    }

    internal LogEntry Append(LogLevel level, DateTime timestamp, string message) {
      var entry = new LogEntry(null, timestamp, level, message);
      string path = FilePath(timestamp.Date);
      lock(_sync) {
        using(var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read)) {
          Header header;
          if(stream.Length == 0) {
            string fileId = Guid.NewGuid().ToString("N");
            byte[] bytes = Utf8.GetBytes(HeaderPrefix + timestamp.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
              + " fileId=" + fileId + "\n");
            stream.Write(bytes, 0, bytes.Length);
            header = new Header(timestamp.Date, fileId, bytes.Length);
          } else {
            var reader = new BlockReader(stream);
            header = ReadHeader(reader, timestamp.Date);
            if(CompleteEnd(reader, header) != reader.Length) {
              throw new InvalidDataException("The log file has an incomplete final record: " + path);
            }
          }
          stream.Seek(0, SeekOrigin.End);
          long offset = stream.Position;
          string[] lines = entry.Message.Split(new[] { '\n' }, StringSplitOptions.None);
          var text = new StringBuilder();
          text.Append(entry.Timestamp.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture));
          text.Append(" [").Append(LevelCode(level)).Append("] ").Append(lines[0]).Append('\n');
          for(int i = 1; i < lines.Length; i++) {
            text.Append("| ").Append(lines[i]).Append('\n');
          }
          text.Append('\n');
          byte[] record = Utf8.GetBytes(text.ToString());
          stream.Write(record, 0, record.Length);
          // Подписчик получает файловый ID только после видимости всей записи читателю.
          stream.Flush();
          return new LogEntry(RecordId(header, offset), entry.Timestamp, level, entry.Message);
        }
      }
    }

    internal IReadOnlyList<LogEntry> Read(string beforeId, int count) {
      long ignored;
      return Read(beforeId, count, out ignored);
    }

    internal IReadOnlyList<LogEntry> Read(string beforeId, int count, out long bytesRead) {
      if(count < 1 || count > MaxCount) {
        throw new ArgumentOutOfRangeException(nameof(count), "Log page size must be between 1 and " + MaxCount);
      }
      Position before = string.IsNullOrEmpty(beforeId) ? null : ParsePosition(beforeId);
      var files = Directory.GetFiles(_directory, "*.log", SearchOption.TopDirectoryOnly)
        .Select(path => new { Path = path, Day = FileDay(path) })
        .Where(f => f.Day.HasValue && (before == null || f.Day.Value <= before.Day))
        .OrderByDescending(f => f.Day.Value).ToArray();
      if(before != null && !files.Any(f => f.Day.Value == before.Day)) {
        throw new ArgumentException("The history boundary file no longer exists", nameof(beforeId));
      }
      var result = new List<LogEntry>(count);
      bytesRead = 0;
      foreach(var file in files) {
        FileStream stream = null;
        BlockReader reader = null;
        try {
          Header header;
          // Не наблюдаем пустой файл между созданием и записью его заголовка.
          lock(_sync) {
            stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            reader = new BlockReader(stream);
            header = ReadHeader(reader, file.Day.Value);
          }
          long end;
          if(before != null && file.Day.Value == before.Day) {
            if(header.FileId != before.FileId || before.Offset < header.End || before.Offset >= reader.Length) {
              throw new ArgumentException("The history boundary has expired or changed", nameof(beforeId));
            }
            ValidatePosition(reader, header, before.Offset);
            end = before.Offset;
          } else {
            end = CompleteEnd(reader, header);
            if(end != reader.Length && file.Day.Value != DateTime.Today) {
              throw new InvalidDataException("Incomplete record in a closed log file: " + file.Path);
            }
          }
          while(end > header.End && result.Count < count) {
            result.Add(ReadPrevious(reader, header, ref end));
          }
        }
        finally {
          if(reader != null) bytesRead += reader.BytesRead;
          if(stream != null) stream.Dispose();
        }
        if(result.Count == count) break;
      }
      result.Reverse();
      return result;
    }

    internal void Prune(DateTime today, Action<string> report) {
      lock(_sync) {
        string[] files;
        try {
          files = Directory.GetFiles(_directory, "*.log", SearchOption.TopDirectoryOnly);
        }
        catch(IOException ex) {
          report("Log retention failed: " + ex.Message);
          return;
        }
        catch(UnauthorizedAccessException ex) {
          report("Log retention failed: " + ex.Message);
          return;
        }
        foreach(string path in files) {
          DateTime? day = FileDay(path);
          if(!day.HasValue) continue;
          try {
            if(File.GetLastWriteTime(path).AddDays(KeepDays) >= today.Date) continue;
            using(var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) {
              try {
                ReadHeader(new BlockReader(stream), day.Value);
              }
              catch(InvalidDataException) {
                // Одного имени недостаточно, чтобы считать файл своим и удалить его.
                continue;
              }
            }
            File.Delete(path);
          }
          catch(IOException ex) {
            report("Log retention could not remove " + path + ": " + ex.Message);
          }
          catch(UnauthorizedAccessException ex) {
            report("Log retention could not remove " + path + ": " + ex.Message);
          }
        }
      }
    }

    private string FilePath(DateTime day) {
      return Path.Combine(_directory, day.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log");
    }

    private static DateTime? FileDay(string path) {
      string name = Path.GetFileName(path);
      DateTime day;
      return name.Length == 12 && name.EndsWith(".log", StringComparison.Ordinal)
        && DateTime.TryParseExact(name.Substring(0, 8), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day)
        ? day : (DateTime?)null;
    }

    private static Header ReadHeader(BlockReader reader, DateTime day) {
      long pos = 0;
      string line = reader.ReadLine(ref pos, 256);
      string prefix = HeaderPrefix + day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " fileId=";
      Guid fileId;
      if(!line.StartsWith(prefix, StringComparison.Ordinal)
          || !Guid.TryParseExact(line.Substring(prefix.Length), "N", out fileId)) {
        throw new InvalidDataException("Unsupported or damaged log header");
      }
      return new Header(day, fileId.ToString("N"), pos);
    }

    private static char LevelCode(LogLevel level) {
      switch(level) {
      case LogLevel.Debug: return 'D';
      case LogLevel.Info: return 'I';
      case LogLevel.Warning: return 'W';
      case LogLevel.Error: return 'E';
      default: throw new ArgumentOutOfRangeException(nameof(level));
      }
    }

    private static LogEntry ParseEntry(Header header, long offset, string line, IList<string> continuation) {
      DateTime time;
      if(line.Length < 17 || line[12] != ' ' || line[13] != '[' || line[15] != ']' || line[16] != ' '
          || !DateTime.TryParseExact(line.Substring(0, 12), "HH:mm:ss.fff", CultureInfo.InvariantCulture,
            DateTimeStyles.NoCurrentDateDefault, out time)) {
        throw new InvalidDataException("Invalid log record at byte " + offset);
      }
      LogLevel level;
      switch(line[14]) {
      case 'D': level = LogLevel.Debug; break;
      case 'I': level = LogLevel.Info; break;
      case 'W': level = LogLevel.Warning; break;
      case 'E': level = LogLevel.Error; break;
      default: throw new InvalidDataException("Invalid log level at byte " + offset);
      }
      var message = new StringBuilder(line.Substring(17));
      foreach(string part in continuation) message.Append('\n').Append(part);
      return new LogEntry(RecordId(header, offset), header.Day.Add(time.TimeOfDay), level, message.ToString());
    }

    private static string RecordId(Header header, long offset) {
      return header.Day.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ":" + header.FileId
        + ":" + offset.ToString("D20", CultureInfo.InvariantCulture);
    }

    private static Position ParsePosition(string id) {
      var parts = id.Split(':');
      DateTime day;
      Guid fileId;
      long offset;
      if(parts.Length != 3 || parts[0].Length != 8 || parts[2].Length != 20
          || !DateTime.TryParseExact(parts[0], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day)
          || !Guid.TryParseExact(parts[1], "N", out fileId)
          || !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out offset)
          || offset < 0 || id != RecordId(new Header(day, fileId.ToString("N"), 0), offset)) {
        throw new ArgumentException("Invalid log record ID", "beforeId");
      }
      return new Position { Day = day, FileId = fileId.ToString("N"), Offset = offset };
    }

    private static long CompleteEnd(BlockReader reader, Header header) {
      for(long pos = reader.Length; pos > header.End + 1; pos--) {
        if(reader.Byte(pos - 1) == '\n' && reader.Byte(pos - 2) == '\n') return pos;
      }
      return header.End;
    }

    private static void ValidatePosition(BlockReader reader, Header header, long offset) {
      if(offset != header.End && (offset < header.End + 2 || reader.Byte(offset - 1) != '\n' || reader.Byte(offset - 2) != '\n')) {
        throw new ArgumentException("The history boundary is not a record start", "beforeId");
      }
      long pos = offset;
      string first = reader.ReadLine(ref pos);
      var parts = new List<string>();
      while(true) {
        string line = reader.ReadLine(ref pos);
        if(line.Length == 0) break;
        if(!line.StartsWith("| ", StringComparison.Ordinal)) {
          throw new InvalidDataException("Invalid log continuation");
        }
        parts.Add(line.Substring(2));
      }
      ParseEntry(header, offset, first, parts);
    }

    private static LogEntry ReadPrevious(BlockReader reader, Header header, ref long pos) {
      long start;
      if(reader.PreviousLine(ref pos, header.End, out start).Length != 0) {
        throw new InvalidDataException("Missing log record terminator");
      }
      var parts = new List<string>();
      while(pos > header.End) {
        string line = reader.PreviousLine(ref pos, header.End, out start);
        if(line.StartsWith("| ", StringComparison.Ordinal)) {
          parts.Add(line.Substring(2));
        } else {
          parts.Reverse();
          return ParseEntry(header, start, line, parts);
        }
      }
      throw new InvalidDataException("Missing log record header");
    }

    private sealed class Position {
      internal DateTime Day;
      internal string FileId;
      internal long Offset;
    }

    private readonly struct Header {
      internal readonly DateTime Day;
      internal readonly string FileId;
      internal readonly long End;
      internal Header(DateTime day, string fileId, long end) {
        Day = day;
        FileId = fileId;
        End = end;
      }
    }

    /// <summary>Фиксирует длину файла и читает блоки; границы UTF-8 декодируются только после сборки строки.</summary>
    private sealed class BlockReader {
      private readonly Stream _stream;
      private readonly byte[] _buffer = new byte[4096];
      private long _start = -1;
      private int _count;
      internal readonly long Length;
      internal long BytesRead;

      internal BlockReader(Stream stream) {
        _stream = stream;
        Length = stream.Length;
      }
      internal byte Byte(long offset) {
        if(offset < 0 || offset >= Length) throw new InvalidDataException("Incomplete log record");
        if(_start < 0 || offset < _start || offset >= _start + _count) {
          _start = offset / _buffer.Length * _buffer.Length;
          _stream.Position = _start;
          _count = 0;
          int wanted = (int)Math.Min(_buffer.Length, Length - _start);
          while(_count < wanted) {
            int read = _stream.Read(_buffer, _count, wanted - _count);
            if(read == 0) throw new IOException("Log file changed while reading");
            _count += read;
            BytesRead += read;
          }
        }
        return _buffer[(int)(offset - _start)];
      }
      internal string ReadLine(ref long pos, int limit = int.MaxValue) {
        var bytes = new List<byte>();
        while(pos < Length) {
          byte b = Byte(pos++);
          if(b == '\n') return Decode(bytes);
          if(bytes.Count >= limit) throw new InvalidDataException("Log header is too long");
          bytes.Add(b);
        }
        throw new InvalidDataException("Incomplete log line");
      }
      internal string PreviousLine(ref long pos, long floor, out long start) {
        if(pos <= floor || Byte(--pos) != '\n') throw new InvalidDataException("Invalid log line boundary");
        var bytes = new List<byte>();
        while(pos > floor && Byte(pos - 1) != '\n') bytes.Add(Byte(--pos));
        start = pos;
        bytes.Reverse();
        return Decode(bytes);
      }
      private static string Decode(List<byte> bytes) {
        try {
          return Utf8.GetString(bytes.ToArray());
        }
        catch(DecoderFallbackException ex) {
          throw new InvalidDataException("Invalid UTF-8 in log file", ex);
        }
      }
    }
  }
}
