///<remarks>Этот файл является частью проекта <see cref="https://github.com/enviriot">Enviriot</see>.<remarks>
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using X13.Repository;
using X13.WebUI;
using JSC = NiL.JS.Core;

namespace X13.Tests {
  /// <summary>Дерево топиков в браузере: что контроллер отправляет клиенту при перемещении топика.</summary>
  /// <remarks>Перемещение объявляется обоим родителям, и обязанности разделены по сторонам: покинутый
  /// родитель снимает строку и поправляет свою стрелку, принимающий добавляет строку и поправляет свою.
  /// Проверяется именно этот раздел, потому что до него обе половины выполнялись в обработчике
  /// принимающей стороны - и не выполнялись вовсе, когда та сторона молчала.
  /// <para>Контроллер берётся настоящий, а подменяется только отправка: список кадров и есть то,
  /// что увидел бы браузер. Очередь движка не подменяется вовсе - без post контроллер выполняет
  /// работу на месте, как и задумано для тестов.</para></remarks>
  [TestFixture]
  public class TopicTreeControllerMoveTests : RepoTestBase {
    private const string View = "workspace";
    private readonly List<JSC.JSObject> _sent = new List<JSC.JSObject>();
    private TopicTreeController _tree;

    [SetUp]
    public void TopicTreeControllerMoveTestsSetUp() {
      _sent.Clear();
      _tree = new TopicTreeController(_sent.Add, new ViewTargetRegistry(), Topic.root, View, t => new ViewRowDto());
      _tree.SendRoot();
      _tree.Expand(Vid("/"), true);
      Tick();
      _sent.Clear();
    }

    [TearDown]
    public void TopicTreeControllerMoveTestsTearDown() {
      if(_tree != null) {
        _tree.Dispose();
        _tree = null;
      }
    }

    /// <summary>Переезд в свёрнутую папку: строка на прежнем месте снимается.</summary>
    /// <remarks>Тот случай, ради которого всё это и делалось. Прежде снятие строки стояло под
    /// раскрытостью ПРИНИМАЮЩЕГО родителя - вопрос был задан не тому: показывает строку покинутый.
    /// Перетащить узел на свёрнутую папку - обычный жест, и он оставлял в дереве строку топика,
    /// которого там уже нет.</remarks>
    [Test]
    public void Move_IntoACollapsedFolderDropsTheRowItLeft() {
      Topic a = root.Get("/a");
      Topic b = root.Get("/b");
      Topic x = root.Get("/a/x");
      Tick();
      _tree.Expand(Vid("/a"), true);   // /b остаётся свёрнутой, но видимой
      Tick();
      _sent.Clear();

      x.Move(b, null);
      Tick();
      Assert.That(Deleted(), Is.EqualTo(new[] { Vid("/a/x") }));
      Assert.That(Expander(Vid("/a")), Is.EqualTo(0));   // последний потомок ушёл
      Assert.That(a.HasChildren(), Is.False);
    }

    /// <summary>Переезд в ветку, которую клиент ни разу не открывал: строка всё равно снимается.</summary>
    /// <remarks>Подписки заводятся на каждую отправленную строку, поэтому у принимающего родителя
    /// её может не быть вовсе - и тогда прежде не выполнялось ничего: ни снятия строки, ни правки
    /// стрелки, поскольку обе половины жили в обработчике, который не вызывался.</remarks>
    [Test]
    public void Move_IntoAnUnwatchedBranchDropsTheRowItLeft() {
      Topic a = root.Get("/a");
      Topic x = root.Get("/a/x");
      Topic deep = root.Get("/b/deep");   // /b свёрнута, поэтому /b/deep клиенту не отправлялась
      Tick();
      _tree.Expand(Vid("/a"), true);
      Tick();
      _sent.Clear();

      x.Move(deep, null);
      Tick();
      Assert.That(Deleted(), Is.EqualTo(new[] { Vid("/a/x") }));
      Assert.That(Added(), Is.Empty);   // принимающая сторона молчит, и это правильно
      Assert.That(Expander(Vid("/a")), Is.EqualTo(0));
    }

    /// <summary>Переименование: старая строка снимается, новая добавляется - обе одним обработчиком.</summary>
    /// <remarks>Родитель не меняется, поэтому событие приходит один раз, и разделение обязанностей
    /// на этот случай не распространяется: vid строится из пути, так что даже переименование на месте
    /// означает для клиента другую строку.</remarks>
    [Test]
    public void Rename_RetiresTheOldRowAndAddsTheNew() {
      Topic a = root.Get("/a");
      Topic x = root.Get("/a/x");
      Tick();
      _tree.Expand(Vid("/a"), true);
      Tick();
      _sent.Clear();

      x.Move(null, "y");
      Tick();
      Assert.That(Deleted(), Is.EqualTo(new[] { Vid("/a/x") }));
      Assert.That(Added(), Is.EqualTo(new[] { Vid("/a/y") }));
      // Обновление родителя не отправляется вовсе: потомок никуда не делся, стрелка прежняя,
      // а SendUpd шлёт кадр только когда в строке что-то изменилось.
      Assert.That(Expander(Vid("/a")), Is.Null);
    }

    /// <summary>Переезд между двумя раскрытыми папками: строка уходит из одной и появляется в другой.</summary>
    [Test]
    public void Move_BetweenTwoExpandedFoldersMovesTheRow() {
      Topic a = root.Get("/a");
      Topic b = root.Get("/b");
      Topic x = root.Get("/a/x");
      root.Get("/b/keep");
      Tick();
      _tree.Expand(Vid("/a"), true);
      _tree.Expand(Vid("/b"), true);
      Tick();
      _sent.Clear();

      x.Move(b, null);
      Tick();
      Assert.That(Deleted(), Is.EqualTo(new[] { Vid("/a/x") }));
      Assert.That(Added(), Is.EqualTo(new[] { Vid("/b/x") }));
      Assert.That(Expander(Vid("/a")), Is.EqualTo(0));   // осталась без потомков
      Assert.That(Expander(Vid("/b")), Is.Null);         // потомок у неё был и раньше: менять нечего
    }

    private static string Vid(string path) {
      return View + "#" + path;
    }
    private string[] Frames(string type) {
      return _sent.Where(f => Text(f, "type") == type).Select(f => Text(f, "vid")).ToArray();
    }
    private string[] Deleted() {
      return Frames(ViewMessageTypes.EvntDel);
    }
    private string[] Added() {
      return Frames(ViewMessageTypes.EvntAdd);
    }
    /// <summary>Стрелка в последнем обновлении строки: 0 - потомков нет, 1 - свёрнута, 2 - раскрыта.</summary>
    private int? Expander(string vid) {
      JSC.JSObject last = _sent.LastOrDefault(f => Text(f, "type") == ViewMessageTypes.EvntUpd && Text(f, "vid") == vid);
      if(last == null) return null;
      JSC.JSValue e = last["expander"];
      return e == null || !e.Defined ? 0 : (int)e;
    }
    private static string Text(JSC.JSObject frame, string key) {
      JSC.JSValue v = frame[key];
      return v == null ? null : v.Value as string;
    }
  }
}
