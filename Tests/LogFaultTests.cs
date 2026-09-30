///<remarks>Этот файл является частью проекта <see cref="https://github.com/enviriot">Enviriot</see>.<remarks>
using System;
using System.Collections.Generic;
using System.Threading;
using NUnit.Framework;

namespace X13.Tests {
  /// <summary>Ограничение частоты повторяющихся сбоев в журнале: суточный итог, хвост серии и кто их выписывает.</summary>
  /// <remarks>Ограничение - это ещё и способ что-то пропустить: капающий сбой получает одну строку, а дальше
  /// тишину, и никто не листает журнал назад, чтобы их пересчитать. Поэтому число выписывается при смене
  /// даты, а хвост серии - когда она затихла.
  /// <para>Тесты работают с собственным экземпляром журнала: вымышленные часы, без файла, свои подписчики -
  /// журнал процесса и строки соседних тестов их не касаются. Экземпляр доставляет записи с потока пула,
  /// поэтому тест ждёт нужную строку, а не любую. Журнал процесса затрагивает один тест - тот, что проверяет
  /// сам Log.Fault.</para>
  /// <para>Окно у экземпляра короткое, WakeMs: вымышленным часам всё равно, а потребитель просыпается по
  /// настоящим, и хвост, который он выписывает сам, приходит за доли секунды, а не за полминуты.</para></remarks>
  [TestFixture]
  public class LogFaultTests {
    /// <summary>Сон потребителя и окно ограничения тестового экземпляра, в миллисекундах.</summary>
    private const int WakeMs = 200;
    private DateTime _now;
    private Log _log;
    private List<string> _lines;
    private List<DateTime> _stamps;
    private ManualResetEventSlim _signal;

    [SetUp]
    public void SetUp() {
      List<string> lines = new List<string>();
      List<DateTime> stamps = new List<DateTime>();
      ManualResetEventSlim signal = new ManualResetEventSlim(false);
      _lines = lines;
      _stamps = stamps;
      _signal = signal;
      _now = new DateTime(2026, 9, 3, 12, 0, 0);
      _log = new Log(null, () => _now, false, WakeMs);
      _log.Written += (ll, dt, msg) => {
        lock(lines) {
          lines.Add(msg);
          stamps.Add(dt);
        }
        signal.Set();
      };
    }

    [TearDown]
    public void TearDown() {
      _log.Close();
      _signal.Dispose();
    }

    /// <summary>Ждёт до десяти секунд строку, в которой есть все части сразу.</summary>
    private bool WaitForLine(List<string> lines, params string[] parts) {
      DateTime deadline = DateTime.UtcNow.AddSeconds(10);
      while(DateTime.UtcNow < deadline) {
        lock(lines) {
          if(lines.Exists(m => m != null && Array.TrueForAll(parts, p => m.Contains(p)))) {
            return true;
          }
        }
        _signal.Wait(100);
        _signal.Reset();
      }
      return false;
    }

    [Test]
    public void TheDayTotalIsWrittenOutWhenTheDateTurnsOver() {
      _log.FlushFaults();   // первый сброс только узнаёт, какой сегодня день

      for(int i = 0; i < 7; i++) {   // первый записывается, шесть удерживаются
        _log.ReportFault(LogLevel.Warning, "Test.Day", null, new InvalidOperationException("day"));
      }
      Assert.That(WaitForLine(_lines, "Test.Day(-) - "), Is.True, "первый сбой серии не записан вовсе");

      _now = _now.AddDays(1);
      _log.FlushFaults();
      Assert.That(WaitForLine(_lines, "Test.Day - 7 failures on 2026-09-03"), Is.True,
        "дата сменилась, а сколько было сбоев, никто не сказал");
    }

    /// <summary>Ничего не случилось - ничего и не сказано.</summary>
    /// <remarks>Суточная строка о нуле сбоев - шум, который приучает пропускать ту строку, что важна.
    /// Метка ставится в очередь после обоих сбросов, а очередь одна и доставляется по порядку: пришла
    /// метка - значит, итог, будь он выписан, пришёл бы раньше неё.</remarks>
    [Test]
    public void ADayWithoutFaultsSaysNothing() {
      _log.FlushFaults();
      _now = _now.AddDays(1);
      _log.FlushFaults();
      _log.Enqueue(LogLevel.Info, "{0}", "marker");

      Assert.That(WaitForLine(_lines, "marker"), Is.True);
      lock(_lines) {
        Assert.That(_lines.Exists(m => m.Contains("failures on")), Is.False);
      }
    }

    /// <summary>Хвост, удержанный через полночь, всё равно выписывается.</summary>
    /// <remarks>Суточный итог забывает ключи, которым больше нечего выписать. Забудь он заодно и ключ
    /// с удержанным хвостом, сбой, случившийся трижды, читался бы как один.</remarks>
    [Test]
    public void AHeldTailSurvivesTheDayTurningOver() {
      _now = new DateTime(2026, 7, 4).AddMilliseconds(-WakeMs / 2);   // полокна до полуночи
      _log.FlushFaults();

      for(int i = 0; i < 3; i++) {   // первый записывается, два удерживаются до середины окна после полуночи
        _log.ReportFault(LogLevel.Warning, "Test.Midnight", null, new InvalidOperationException("midnight"));
      }
      _now = _now.AddMilliseconds(WakeMs * 6 / 10);   // полночь прошла, а окно хвоста ещё не истекло
      _log.FlushFaults();
      Assert.That(WaitForLine(_lines, "Test.Midnight - 3 failures on 2026-07-03"), Is.True,
        "итог за прошедшие сутки не записан");

      _now = _now.AddMilliseconds(WakeMs);   // а теперь истекло и окно
      _log.FlushFaults();
      Assert.That(WaitForLine(_lines, "Test.Midnight(-) [and 1 more]", "no longer occurring"), Is.True,
        "хвост, удержанный через смену даты, потерян");
    }

    /// <summary>Экземпляр живёт по своим часам: ими помечены записи, по ним же потребитель сам меняет сутки.</summary>
    /// <remarks>FlushFaults тест не вызывает: сбросить обязан потребитель. Метка в начале нужна и для
    /// проверки времени, и затем, чтобы потребитель успел узнать сутки до сбоев: его проход сбрасывает
    /// сбои раньше, чем доставляет очередь.</remarks>
    [Test]
    public void TheConsumerTurnsTheDayByTheInstanceClock() {
      _log.Enqueue(LogLevel.Info, "{0}", "marker");
      Assert.That(WaitForLine(_lines, "marker"), Is.True);
      lock(_lines) {
        Assert.That(_stamps[_lines.FindIndex(m => m.Contains("marker"))], Is.EqualTo(_now));
      }

      for(int i = 0; i < 2; i++) {   // первый записывается, второй удерживается
        _log.ReportFault(LogLevel.Warning, "Test.Clock", null, new InvalidOperationException("clock"));
      }
      _now = _now.AddDays(1);

      Assert.That(WaitForLine(_lines, "Test.Clock - 2 failures on 2026-09-03"), Is.True,
        "сутки экземпляра сменились, а итога по сбоям нет");
    }

    /// <summary>Хвост затихшей серии потребитель выписывает сам, без FlushFaults.</summary>
    /// <remarks>"[and 1 more]", а не "[and 2 more]": строка хвоста сама описывает последний удержанный сбой,
    /// так что три сбоя - это первая строка, хвост и ещё один.</remarks>
    [Test]
    public void TheConsumerWritesTheTailByItself() {
      for(int i = 0; i < 3; i++) {   // первый записывается, два удерживаются
        _log.ReportFault(LogLevel.Warning, "Test.Tail", null, new InvalidOperationException("tail"));
      }
      _now = _now.AddMilliseconds(WakeMs);   // окно истекло; дальше - только пробуждение потребителя

      Assert.That(WaitForLine(_lines, "Test.Tail(-) [and 1 more]", "no longer occurring"), Is.True,
        "серия затихла, а хвост так никто и не выписал");
    }

    /// <summary>Статический Log.Fault доходит до журнала процесса.</summary>
    /// <remarks>Единственный тест на журнале процесса, поэтому время настоящее, а сбой помечен guid:
    /// строки соседних тестов нельзя принять за свои. Файл на время теста выключен - каталог ../log у тестов
    /// и отладочного сервера общий. Сбой один: удержанный хвост журнал процесса выписал бы через полминуты,
    /// когда файл уже снова включён.</remarks>
    [Test]
    public void LogFaultReachesTheProcessLog() {
      string tag = "throttle" + Guid.NewGuid().ToString("N");
      List<string> lines = new List<string>();
      ManualResetEventSlim signal = _signal;
      Action<LogLevel, DateTime, string> onLog = (ll, dt, msg) => {
        lock(lines) {
          lines.Add(msg);
        }
        signal.Set();
      };
      bool useFile = X13.Log.UseFile;
      X13.Log.UseFile = false;
      X13.Log.Write += onLog;
      try {
        X13.Log.Fault(LogLevel.Warning, "Test." + tag, null, new InvalidOperationException(tag));

        Assert.That(WaitForLine(lines, "Test." + tag + "(-) - ", "InvalidOperationException"), Is.True,
          "Log.Fault до журнала процесса не дошёл");
      }
      finally {
        X13.Log.Write -= onLog;
        X13.Log.UseFile = useFile;
      }
    }
  }
}
