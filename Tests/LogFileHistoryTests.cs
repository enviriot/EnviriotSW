using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace X13.Tests {
  [TestFixture]
  public class LogFileHistoryTests {
    private string _directory;
    private FileLogHistory _files;

    [SetUp]
    public void CreateDirectory() {
      _directory = Path.Combine(Path.GetTempPath(), "X13.LogHistory." + Guid.NewGuid().ToString("N"));
      _files = new FileLogHistory(_directory);
    }

    [TearDown]
    public void RemoveDirectory() {
      Directory.Delete(_directory, true);
    }

    private string PathFor(DateTime day) {
      return Path.Combine(_directory, day.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log");
    }

    private LogEntry Append(string message, DateTime? timestamp = null, LogLevel level = LogLevel.Info) {
      return _files.Append(level, timestamp ?? DateTime.Today.AddHours(12), message);
    }

    private static void Fails<T>(Action action) where T : Exception {
      Assert.That(Assert.Throws<T>(action), Is.Not.Null);
    }

    [Test]
    public void ReadableFormatHasDateOnlyNameAndStableIdentity() {
      DateTime stamp = DateTime.Today.AddHours(8).AddMinutes(57).AddSeconds(12).AddMilliseconds(345);
      var first = Append("first\r\nsecond\r\n", stamp);
      string path = PathFor(stamp);
      string text = File.ReadAllText(path);
      Assert.That(Directory.GetFiles(_directory).Select(Path.GetFileName), Is.EqualTo(new[] { stamp.ToString("yyyyMMdd") + ".log" }));
      Assert.That(text, Does.StartWith("# EnviriotLog v2 date=" + stamp.ToString("yyyy-MM-dd") + " fileId="));
      Assert.That(text, Does.Contain("08:57:12.345 [I] first\n| second\n| \n\n"));
      Assert.That(text, Does.Not.Contain('\r'));
      var reopened = new FileLogHistory(_directory);
      var second = reopened.Append(LogLevel.Debug, stamp, "second record");
      Assert.That(first.Id.Split(':')[1], Is.EqualTo(second.Id.Split(':')[1]));
      Assert.That(reopened.Read(null, 50).Select(z => z.Id), Is.EqualTo(new[] { first.Id, second.Id }));
      Assert.That(first.Timestamp.Kind, Is.EqualTo(DateTimeKind.Unspecified));
    }

    [TestCase("")]
    [TestCase("\n")]
    [TestCase("a\r\n\r\n| own prefix\rtrailing\n")]
    [TestCase("first\n08:57:12.345 [E] not another record\n    at Stack.Frame()")]
    public void MultilineMessagesRoundTrip(string message) {
      var entry = Append(message);
      var restored = _files.Read(null, 50).Single();
      Assert.That(restored.Message, Is.EqualTo(message.Replace("\r\n", "\n").Replace('\r', '\n')));
      Assert.That(restored.Id, Is.EqualTo(entry.Id));
    }

    [Test]
    public void Utf8AndRecordBoundariesCanCrossReadBlocks() {
      string message = new string('я', 4101) + "\n| граница 😀\n" + new string('界', 4201);
      Append(message);
      Assert.That(_files.Read(null, 50).Single().Message, Is.EqualTo(message));
    }

    [Test]
    public void SameTimestampAndMessageDoNotLoseRecordsBetweenPages() {
      var expected = Enumerable.Range(0, 6).Select(_ => Append("same")).ToArray();
      var found = new List<string>();
      string before = null;
      int requests = 0;
      while(true) {
        var page = _files.Read(before, 2);
        requests++;
        if(page.Count == 0) break;
        found.InsertRange(0, page.Select(z => z.Id));
        before = page[0].Id;
        Assert.That(requests, Is.LessThan(10));
      }
      Assert.That(requests, Is.EqualTo(4));
      Assert.That(found, Is.EqualTo(expected.Select(z => z.Id)));
      Assert.That(found.Distinct().Count(), Is.EqualTo(6));
    }

    [Test]
    public void FilesAndOffsetsDetermineOrderNotWallClock() {
      DateTime today = DateTime.Today;
      Append("first", today.AddHours(10));
      Append("clock moved back", today.AddHours(9));
      Append("tomorrow", today.AddDays(1));
      Append("later append today", today.AddHours(11));
      Assert.That(_files.Read(null, 50).Select(z => z.Message),
        Is.EqualTo(new[] { "first", "clock moved back", "later append today", "tomorrow" }));
    }

    [Test]
    public void ConcurrentAppendDoesNotMoveTheBeforeIdBoundary() {
      var expected = Enumerable.Range(0, 30).Select(i => Append(i.ToString())).ToArray();
      var page = _files.Read(null, 10);
      var writer = Task.Run(() => {
        for(int i = 30; i < 80; i++) Append(i.ToString());
      });
      var older = _files.Read(page[0].Id, 10);
      Assert.That(writer.Wait(5000), Is.True);
      Assert.That(older.Select(z => z.Id), Is.EqualTo(expected.Skip(10).Take(10).Select(z => z.Id)));
    }

    [Test]
    public void IncompleteCurrentTailIsNotReturnedOrOverwritten() {
      var entry = Append("complete");
      string path = PathFor(DateTime.Today);
      using(var stream = new FileStream(path, FileMode.Append, FileAccess.Write)) {
        byte[] tail = Encoding.UTF8.GetBytes("12:00:01.000 [I] partial ");
        stream.Write(tail, 0, tail.Length);
        stream.WriteByte(0xd0);
      }
      byte[] before = File.ReadAllBytes(path);
      Assert.That(_files.Read(null, 50).Single().Id, Is.EqualTo(entry.Id));
      Fails<InvalidDataException>(() => Append("cannot append"));
      Assert.That(File.ReadAllBytes(path), Is.EqualTo(before));
    }

    [Test]
    public void IncompleteClosedFileReportsAnError() {
      DateTime day = DateTime.Today.AddDays(-1);
      Append("complete", day);
      File.AppendAllText(PathFor(day), "12:00:00.000 [I] partial");
      Fails<InvalidDataException>(() => _files.Read(null, 50));
    }

    [Test]
    public void OldFilesAreIgnoredAndForeignDateFileIsNotOverwritten() {
      string old = Path.Combine(_directory, "261111_enviriot.log");
      File.WriteAllText(old, "old format");
      Assert.That(_files.Read(null, 50), Is.Empty);
      string foreign = PathFor(DateTime.Today);
      File.WriteAllText(foreign, "foreign format\n");
      Fails<InvalidDataException>(() => Append("test"));
      Fails<InvalidDataException>(() => _files.Read(null, 50));
      Assert.That(File.ReadAllText(foreign), Is.EqualTo("foreign format\n"));
      Assert.That(File.ReadAllText(old), Is.EqualTo("old format"));
    }

    [TestCase("../other.log")]
    [TestCase("20261111:invalid:00000000000000000001")]
    [TestCase("20261111:00000000000000000000000000000000:-1")]
    public void InvalidBeforeIdIsRejected(string id) {
      Fails<ArgumentException>(() => _files.Read(id, 50));
    }

    [TestCase(0)]
    [TestCase(201)]
    public void InvalidPageSizeIsRejected(int count) {
      Fails<ArgumentOutOfRangeException>(() => _files.Read(null, count));
    }

    [Test]
    public void CompleteButMalformedRecordsReportErrors() {
      Append("valid");
      string path = PathFor(DateTime.Today);
      File.AppendAllText(path, "not a record\n\n");
      Fails<InvalidDataException>(() => _files.Read(null, 50));
    }

    [Test]
    public void CompleteInvalidUtf8IsNotSilentlyReplaced() {
      Append("valid");
      string path = PathFor(DateTime.Today);
      using(var stream = new FileStream(path, FileMode.Append, FileAccess.Write)) {
        byte[] prefix = Encoding.UTF8.GetBytes("12:00:00.000 [I] ");
        stream.Write(prefix, 0, prefix.Length);
        stream.WriteByte(0xd0);
        stream.WriteByte(10);
        stream.WriteByte(10);
      }
      Fails<InvalidDataException>(() => _files.Read(null, 50));
    }

    [Test]
    public void AccessFailureIsNotAnEmptyHistory() {
      Append("valid");
      using(var held = new FileStream(PathFor(DateTime.Today), FileMode.Open, FileAccess.Read, FileShare.None)) {
        Fails<IOException>(() => _files.Read(null, 50));
      }
    }

    [Test]
    public void DeletedRecreatedAndNonBoundaryIdsAreRejected() {
      var entry = Append("value");
      string[] parts = entry.Id.Split(':');
      parts[2] = (long.Parse(parts[2], CultureInfo.InvariantCulture) + 1).ToString("D20", CultureInfo.InvariantCulture);
      Fails<ArgumentException>(() => _files.Read(string.Join(":", parts), 50));
      File.Delete(PathFor(DateTime.Today));
      Fails<ArgumentException>(() => _files.Read(entry.Id, 50));
      var replacement = Append("replacement");
      Assert.That(replacement.Id, Is.Not.EqualTo(entry.Id));
      Fails<ArgumentException>(() => _files.Read(entry.Id, 50));
    }

    [Test]
    public void LargeFileIsReadFromItsTailInBoundedBlocks() {
      Append("first");
      string path = PathFor(DateTime.Today);
      byte[] record = Encoding.UTF8.GetBytes("12:00:00.000 [I] " + new string('x', 100) + "\n\n");
      using(var stream = new FileStream(path, FileMode.Append, FileAccess.Write)) {
        for(int i = 0; i < 50000; i++) stream.Write(record, 0, record.Length);
      }
      long bytes;
      Assert.That(_files.Read(null, 3, out bytes).Count, Is.EqualTo(3));
      Assert.That(new FileInfo(path).Length, Is.GreaterThan(5 * 1024 * 1024));
      Assert.That(bytes, Is.LessThan(32768));
    }

    [Test]
    public void RetentionOnlyDeletesOwnedFilesOlderThan36Days() {
      DateTime today = DateTime.Today;
      Append("expired", today.AddDays(-40));
      Append("boundary", today.AddDays(-39));
      File.SetLastWriteTime(PathFor(today.AddDays(-40)), today.AddDays(-37));
      File.SetLastWriteTime(PathFor(today.AddDays(-39)), today.AddDays(-36));
      string old = Path.Combine(_directory, "200101_enviriot.log");
      string foreign = PathFor(new DateTime(2000, 1, 1));
      File.WriteAllText(old, "legacy");
      File.WriteAllText(foreign, "foreign\n");
      File.SetLastWriteTime(old, today.AddDays(-100));
      File.SetLastWriteTime(foreign, today.AddDays(-100));
      var errors = new List<string>();
      _files.Prune(today, errors.Add);
      Assert.That(errors, Is.Empty);
      Assert.That(File.Exists(PathFor(today.AddDays(-40))), Is.False);
      Assert.That(File.Exists(PathFor(today.AddDays(-39))), Is.True);
      Assert.That(File.Exists(old), Is.True);
      Assert.That(File.Exists(foreign), Is.True);
    }

    [Test]
    public void LiveFileIdIsImmediatelyReadable() {
      var log = new Log(_directory, () => DateTime.Today.AddHours(12), true, 200);
      using(var arrived = new ManualResetEventSlim()) {
        LogEntry live = null, saved = null;
        log.EntryWritten += entry => {
          live = entry;
          saved = log.ReadFileHistory(null, 50).Single();
          arrived.Set();
        };
        try {
          log.Enqueue(LogLevel.Info, "Привет\n| мир");
          Assert.That(arrived.Wait(5000), Is.True);
          Assert.That(live.Id, Is.Not.Null);
          Assert.That(saved.Id, Is.EqualTo(live.Id));
          Assert.That(saved.Message, Is.EqualTo(live.Message));
          Assert.That(saved.Timestamp, Is.EqualTo(live.Timestamp));
        }
        finally {
          log.Close();
        }
      }
    }

    [Test]
    public void DisabledFileLoggingHasLiveOnlyEntriesAndNoHistory() {
      Append("already on disk");
      var log = new Log(_directory, null, false, 200);
      using(var arrived = new ManualResetEventSlim()) {
        LogEntry live = null;
        log.EntryWritten += entry => { live = entry; arrived.Set(); };
        try {
          Fails<InvalidOperationException>(() => log.ReadFileHistory(null, 50));
          log.Enqueue(LogLevel.Debug, "live");
          Assert.That(arrived.Wait(5000), Is.True);
          Assert.That(live.Id, Is.Null);
          Assert.That(live.Level, Is.EqualTo(LogLevel.Debug));
          Assert.That(_files.Read(null, 50).Count, Is.EqualTo(1));
        }
        finally {
          log.Close();
        }
      }
    }

    [Test]
    public void FileFailureIsReportedWithoutRecursiveLogging() {
      string path = PathFor(DateTime.Today);
      File.WriteAllText(path, "foreign\n");
      var log = new Log(_directory, null, true, 200);
      var entries = new List<LogEntry>();
      using(var arrived = new ManualResetEventSlim()) {
        log.EntryWritten += entry => {
          lock(entries) {
            entries.Add(entry);
            if(entries.Count == 3) arrived.Set();
          }
        };
        try {
          log.Enqueue(LogLevel.Info, "one");
          log.Enqueue(LogLevel.Info, "two");
          Assert.That(arrived.Wait(5000), Is.True);
          lock(entries) {
            Assert.That(entries.Count(z => z.Level == LogLevel.Error), Is.EqualTo(1));
            Assert.That(entries.Select(z => z.Id), Is.All.Null);
          }
          Assert.That(File.ReadAllText(path), Is.EqualTo("foreign\n"));
        }
        finally {
          log.Close();
        }
      }
    }
  }
}
