///<remarks>Этот файл является частью проекта <see cref="https://github.com/enviriot">Enviriot</see>.<remarks>
using System.Collections.Generic;
using System.Linq;
using System.Net;
using NUnit.Framework;
using X13.Repository;
using X13.WebUI;
using X13.WebUI.Helpers;

namespace X13.Tests {
  /// <summary>Дашборд и перемещение топика: что уходит клиенту, когда подписанный путь исчезает.</summary>
  /// <remarks>Протокол дашборда адресует значения путём, поэтому перемещение для него - это
  /// исчезновение одного пути и появление другого. Прежний путь снимается тем же "null", которым
  /// протокол записывает удаление, иначе привязанный к нему виджет показывает последнее значение,
  /// пока открыта страница.
  /// <para>Сессия берётся настоящая, подменены только отправка и очередь: без post она выполняет
  /// работу на месте - режим, ради тестов и предусмотренный. Права объявляются голым адресом,
  /// а не "local": тот раскрывается в подсети машины и на разных машинах означает разное.</para></remarks>
  [TestFixture]
  public class DashboardSessionMoveTests : RepoTestBase {
    private readonly List<string> _sent = new List<string>();
    private DashboardSession _session;

    [SetUp]
    public void DashboardSessionMoveTestsSetUp() {
      _sent.Clear();
      DashboardAcl.Start();
      _session = new DashboardSession(_sent.Add, IPAddress.Loopback, "token");
    }

    [TearDown]
    public void DashboardSessionMoveTestsTearDown() {
      if(_session != null) {
        _session.Dispose();
        _session = null;
      }
      DashboardAcl.Stop();
    }

    /// <summary>Топик уехал из поддерева подписки: прежний путь снимается, новый приходит.</summary>
    /// <remarks>Новый путь отправляется потому, что права на него у клиента есть - они проверяются
    /// по каждому значению отдельно, а не только при подписке. Подписки на него клиент не делал,
    /// но протокол адресует значения путём, и виджет, привязанный к новому месту, получит своё.</remarks>
    [Test]
    public void Move_OutOfTheSubscribedSubtreeRetiresTheOldPath() {
      Topic x = root.Get("/dash/x");
      Topic away = Allow("/away");
      Allow("/dash");
      x.SetState(Num(42));
      Tick();
      _session.Handle("S\t/dash/+");
      Tick();
      _sent.Clear();

      x.Move(away, null);
      Tick();
      Assert.That(Published("/dash/x"), Is.EqualTo(new[] { "null" }));
      Assert.That(Published("/away/x"), Is.EqualTo(new[] { "42" }));
    }

    /// <summary>Переименование на месте: старый путь снимается, новый приходит со значением.</summary>
    /// <remarks>Оба кадра идут одному и тому же подписчику и в этом порядке: сначала клиент забывает
    /// прежний путь, затем получает новый. Обратный порядок на мгновение оставил бы виджет с двумя
    /// живыми путями, и какой из них показать - зависело бы от того, как клиент их складывает.</remarks>
    [Test]
    public void Rename_RetiresTheOldPathAndSendsTheNewOne() {
      Topic x = root.Get("/dash/x");
      Allow("/dash");
      x.SetState(Num(42));
      Tick();
      _session.Handle("S\t/dash/+");
      Tick();
      _sent.Clear();

      x.Move(null, "y");
      Tick();
      Assert.That(Published("/dash/x"), Is.EqualTo(new[] { "null" }));
      Assert.That(Published("/dash/y"), Is.EqualTo(new[] { "42" }));
      Assert.That(_sent.IndexOf("P\t/dash/x\tnull"), Is.LessThan(_sent.IndexOf("P\t/dash/y\t42")));
    }

    /// <summary>Топик уехал туда, куда этому клиенту читать нельзя: прежний путь всё равно снимается.</summary>
    /// <remarks>Ради этого случая снятие стоит до проверки прав на новый путь. Забыть показанное -
    /// не чтение, а вот оставить значение под путём, которого больше нет, - прямая неправда.</remarks>
    [Test]
    public void Move_IntoAForbiddenBranchStillRetiresTheOldPath() {
      Topic x = root.Get("/dash/x");
      Allow("/dash");
      Topic secret = root.Get("/secret");   // без объявления прав: доступа нет
      x.SetState(Num(42));
      Tick();
      _session.Handle("S\t/dash/+");
      Tick();
      _sent.Clear();

      x.Move(secret, null);
      Tick();
      Assert.That(Published("/dash/x"), Is.EqualTo(new[] { "null" }));
      Assert.That(Published("/secret/x"), Is.Empty);
    }

    /// <summary>Топик, которого клиент и не видел, снятия не вызывает.</summary>
    /// <remarks>Подписка здесь настоящая и работающая - права на базу объявлены, а перемещаемый
    /// топик просто лежит в другом месте. Иначе тест проходил бы от того, что подписки нет вовсе,
    /// и не проверял бы ничего.</remarks>
    [Test]
    public void Move_OfAnUnreadableTopicSendsNothing() {
      Allow("/dash");
      Topic keep = root.Get("/dash/keep");
      Topic x = root.Get("/hidden/x");   // прав нет ни на прежнем пути, ни на новом
      Tick();
      _session.Handle("S\t/dash/+");
      Tick();
      _sent.Clear();

      x.Move(root.Get("/hidden2"), null);
      Tick();
      Assert.That(_sent, Is.Empty);

      keep.SetState(Num(7));   // а своё подписка получает
      Tick();
      Assert.That(Published("/dash/keep"), Is.EqualTo(new[] { "7" }));
    }

    /// <summary>Топик с объявленным правом чтения для этого клиента.</summary>
    private Topic Allow(string path) {
      Topic t = root.Get(path);
      t.SetField(DashboardAcl.FieldRO, Str(IPAddress.Loopback.ToString()));
      return t;
    }

    /// <summary>Значения, отправленные клиенту для этого пути, в порядке отправки.</summary>
    private string[] Published(string path) {
      string prefix = "P\t" + path + "\t";
      return _sent.Where(f => f.StartsWith(prefix)).Select(f => f.Substring(prefix.Length)).ToArray();
    }
  }
}
