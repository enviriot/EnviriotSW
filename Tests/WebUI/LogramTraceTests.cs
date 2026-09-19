///<remarks>Этот файл является частью проекта <see cref="https://github.com/enviriot">Enviriot</see>.<remarks>
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using X13.Repository;
using X13.WebUI;
using JSC = NiL.JS.Core;

namespace X13.Tests {
  /// <summary>Trace у переменной лограммы: что контроллер отправляет клиенту и что предлагает меню.</summary>
  /// <remarks>Переменная - это один топик с двумя точками, а не блок с пинами, поэтому её строка и есть
  /// её пиновая строка: она одна несёт и цвет, и метку значения. Прежде Trace существовал только для
  /// пинов блока, и три разных места об этом знали по-разному - меню не предлагало пункт, снимок не
  /// отправлял поле, а живое обновление гасило его флагом supportsTrace.
  /// <para>Контроллер берётся настоящий, подменяется только отправка: список кадров и есть то, что
  /// увидел бы браузер. Очередь движка не подменяется - без post контроллер работает на месте.</para>
  /// <para>Значения 42 и 43 выбраны нарочно: у них один и тот же цвет (ColorForValue, оба больше нуля),
  /// поэтому кадр на изменение значения может появиться только из-за displayValue.</para></remarks>
  [TestFixture]
  public class LogramTraceTests : RepoTestBase {
    private const string View = "logram";
    private readonly List<JSC.JSObject> _sent = new List<JSC.JSObject>();
    private LogramGraphController _graph;
    private Topic _traced;
    private Topic _plain;
    private Topic _block;

    [SetUp]
    public void LogramTraceTestsSetUp() {
      _sent.Clear();

      // Блок от переменной отличает только схема пинов в состоянии типового топика: запись Children
      // с ненулевым ddr. У переменной типа нет вовсе - ExecuteAddVariable его не заводит.
      Topic andType = root.Get("/$YS/TYPES/LoBlock/Binary/AND");
      andType.SetState(JsLib.ParseJson("{\"Children\":{\"A\":{\"ddr\":-1},\"Q\":{\"ddr\":1}}}"));

      Topic diagram = root.Get("/L1");
      _block = root.Get("/L1/b1");
      _block.SetField("type", Str("LoBlock/Binary/AND"));
      root.Get("/L1/b1/A");
      root.Get("/L1/b1/Q");

      _traced = root.Get("/L1/v1");
      _traced.SetState(Num(42));
      _traced.SetField("Logram.trace", new NiL.JS.BaseLibrary.Boolean(true));
      _traced.SetField("Logram.left", Num(3));
      _traced.SetField("Logram.top", Num(2));

      _plain = root.Get("/L1/v2");
      _plain.SetState(Num(7));
      _plain.SetField("Logram.left", Num(3));
      _plain.SetField("Logram.top", Num(5));
      Tick();

      _graph = new LogramGraphController(_sent.Add, diagram, View);
      _graph.Open();
    }

    [TearDown]
    public void LogramTraceTestsTearDown() {
      if(_graph != null) {
        _graph.Dispose();
        _graph = null;
      }
    }

    /// <summary>Снимок трассируемой переменной несёт флаг и готовую строку значения.</summary>
    [Test]
    public void Snapshot_TracedVariableCarriesTheLabel() {
      JSC.JSObject row = Added(Vid("/L1/v1"));
      Assert.That(row, Is.Not.Null);
      Assert.That(Flag(row, "trace"), Is.True);
      Assert.That(Text(row, "displayValue"), Is.EqualTo("42"));
    }

    /// <summary>У нетрассируемой переменной обоих полей нет вовсе.</summary>
    /// <remarks>Подавление умолчания, как у пиновой строки: выключенный Trace - обычное состояние
    /// почти каждой переменной, и платить за него полем в каждом кадре незачем.</remarks>
    [Test]
    public void Snapshot_UntracedVariableSaysNothingAboutTrace() {
      JSC.JSObject row = Added(Vid("/L1/v2"));
      Assert.That(row, Is.Not.Null);
      Assert.That(Flag(row, "trace"), Is.False);
      Assert.That(Text(row, "displayValue"), Is.Null);
    }

    /// <summary>Перетаскивание не гасит метку.</summary>
    /// <remarks>Тот случай, ради которого поле пришлось добавить именно в SerializeElementRow, а не
    /// оставить на SendTraceUpdate: строка элемента уходит как evnt.add, который ЗАМЕНЯЕТ строку на
    /// клиенте, а x/y входят в ElementFingerprint - то есть после первого же перетаскивания метка
    /// исчезала бы до следующего переключения Trace.</remarks>
    [Test]
    public void Drag_KeepsTheLabelOnTheReplacedRow() {
      _sent.Clear();
      _traced.SetField("Logram.left", Num(9));
      Tick();

      JSC.JSObject row = Added(Vid("/L1/v1"));
      Assert.That(row, Is.Not.Null, "строка пересылается целиком: изменился x");
      Assert.That(Flag(row, "trace"), Is.True);
      Assert.That(Text(row, "displayValue"), Is.EqualTo("42"));
    }

    /// <summary>Новое значение трассируемой переменной доезжает до метки.</summary>
    /// <remarks>Защита от возврата supportsTrace: прежде переменная получала этот флаг равным false,
    /// и displayValue у неё не пересчитывался никогда. Цвет у 42 и 43 один и тот же, поэтому кадр
    /// здесь появляется исключительно из-за изменившейся строки значения.</remarks>
    [Test]
    public void Value_TracedVariableGetsANewDisplayValue() {
      _sent.Clear();
      _traced.SetState(Num(43));
      Tick();

      JSC.JSObject upd = Updated(Vid("/L1/v1"));
      Assert.That(upd, Is.Not.Null);
      Assert.That(Text(upd, "displayValue"), Is.EqualTo("43"));
    }

    /// <summary>Переключение Trace доезжает до клиента и для переменной.</summary>
    [Test]
    public void Toggle_OffRestatesTheFlag() {
      _sent.Clear();
      _traced.SetField("Logram.trace", new NiL.JS.BaseLibrary.Boolean(false));
      Tick();

      JSC.JSObject upd = Updated(Vid("/L1/v1"));
      Assert.That(upd, Is.Not.Null);
      Assert.That(Flag(upd, "trace"), Is.False, "выключение обязано назваться: это единственный кадр о нём");
    }

    /// <summary>Пункт меню есть у переменной и нет у блока.</summary>
    /// <remarks>У блока собственное состояние клиенту не отправляется вовсе, поэтому флагу там нечего
    /// было бы показывать; его значения несут пины, у каждого свой Trace.</remarks>
    [Test]
    public void Menu_VariableOffersTraceAndBlockDoesNot() {
      MenuItemDto traced = LogramPaletteBuilder.BuildElementMenu(_traced).FirstOrDefault(i => i.Cmd == "trace");
      Assert.That(traced, Is.Not.Null);
      Assert.That(traced.Checked, Is.True);

      MenuItemDto plain = LogramPaletteBuilder.BuildElementMenu(_plain).FirstOrDefault(i => i.Cmd == "trace");
      Assert.That(plain, Is.Not.Null);
      Assert.That(plain.Checked, Is.False);

      Assert.That(LogramPaletteBuilder.BuildElementMenu(_block).Any(i => i.Cmd == "trace"), Is.False);
    }

    private static string Vid(string path) {
      return View + "#" + path;
    }
    private JSC.JSObject Frame(string type, string vid) {
      return _sent.LastOrDefault(f => Text(f, "type") == type && Text(f, "vid") == vid);
    }
    private JSC.JSObject Added(string vid) {
      return Frame(ViewMessageTypes.EvntAdd, vid);
    }
    private JSC.JSObject Updated(string vid) {
      return Frame(ViewMessageTypes.EvntUpd, vid);
    }
    private static string Text(JSC.JSObject frame, string key) {
      JSC.JSValue v = frame[key];
      return v == null ? null : v.Value as string;
    }
    /// <summary>Поле-флаг кадра: отсутствующее поле - это выключено, как и явное false.</summary>
    private static bool Flag(JSC.JSObject frame, string key) {
      JSC.JSValue v = frame[key];
      return v != null && v.Defined && (bool)v;
    }
  }
}
