///<remarks>Этот файл является частью проекта <see cref="https://github.com/enviriot">Enviriot</see>.<remarks>
using System.Linq;
using NUnit.Framework;
using X13.LogramPLC;
using X13.Repository;
using JSC = NiL.JS.Core;

namespace X13.Tests {
  /// <summary>Схема диаграммы pa1 для редактора: возможные пины - каталог mi, показанные - LPLC.in/out.</summary>
  /// <remarks>Выходы Func пишет MQTT_SN по разбору компилятора, так что IDE src не разбирает.</remarks>
  [TestFixture]
  public class LplcSchemaTests : RepoTestBase {
    private Topic _dev, _pa1;

    private static JSC.JSValue Json(string text) {
      return JsLib.ParseJson(text.Replace('\'', '"'));
    }
    private Topic Func(string name, string src) {
      var t = _pa1.Get(name, true);
      t.SetField("type", LP_Tree.FUNC);
      t.SetField(LP_Tree.FLD, Json("{'pos':{'top':0,'left':0},'in':{'A':''},'out':{'Q':true}}"));
      t.SetState(Str(src));
      return t;
    }
    private static void ImportTypes() {
      using (var rs = typeof(LP_Tree).Assembly.GetManifestResourceStream(LP_Tree.TYPES_RESOURCE)) {
        using (var reader = new System.IO.StreamReader(rs)) {
          Xst.Import(reader, null);
        }
      }
    }
    /// <summary>Подписка отвечает тиком позже, а её обработчик пишет ещё тиком позже.</summary>
    private void Ticks() {
      for(int i = 0; i < 4; i++) {
        Tick();
      }
    }
    private static string[] Keys(JSC.JSValue o) {
      return o.IsObject() ? o.Select(kv => kv.Key).ToArray() : new string[0];
    }

    [SetUp]
    public void Tree() {
      _dev = root.Get("/dev/node1", true);
      _pa1 = _dev.Get(LP_Schema.PA1, true);
      _pa1.SetField("type", LP_Schema.PLC_TYPE);
      Tick();
    }

    [Test]
    public void ParseFunc_ListsEachOutputOnceInOrder() {
      var f = LP_DiagramBinder.ParseFunc("this.M = A;\nthis.N = 2;\nthis.M += 1;\n// this.X = 0;\nreturn 'this.Y = 1';");
      using (Assert.EnterMultipleScope()) {
        Assert.That(f.Error, Is.Null);
        Assert.That(f.Outputs, Is.EqualTo(new[] { "Q", "M", "N" }));
      }
    }

    [Test]
    public void ParseFunc_ReportsASyntaxError() {
      var f = LP_DiagramBinder.ParseFunc("this.M = ;");
      Assert.That(f.Error, Is.Not.Null);
    }

    /// <summary>pa1 узнаётся по своему типу: pa0 с Ext/DevicePLC и pa1 без типа - не программы LPLC.</summary>
    [Test]
    public void IsPa1_NeedsTheType() {
      var other = _dev.Get("pa0", true);
      other.SetField("type", "Ext/DevicePLC");
      var untyped = root.Get("/dev/node2/pa1", true);
      Tick();
      using (Assert.EnterMultipleScope()) {
        Assert.That(LP_Schema.IsPa1(_pa1), Is.True);
        Assert.That(LP_Schema.IsPa1(other), Is.False, "pa0");
        Assert.That(LP_Schema.IsPa1(untyped), Is.False, "тип");
      }
    }

    /// <summary>Тип pa1 - из LPLC.xst: кнопки и редактор PLC, как у Ext/DevicePLC, и меню Add блоков LPLC.</summary>
    [Test]
    public void Type_GivesThePlcEditorAndTheBlockMenu() {
      ImportTypes();
      Tick();
      var s = root.Get("/$YS/TYPES/" + LP_Schema.PLC_TYPE, false).GetState();
      using (Assert.EnterMultipleScope()) {
        Assert.That(s.AsString("editor", null), Is.EqualTo("DevicePLC"));
        Assert.That(s.AsString("Children", null), Is.EqualTo(LP_Tree.TYPES));
        Assert.That(s.Field("Action").Select(kv => kv.Value.AsString("name", null)),
          Is.EqualTo(new[] { "MQTT_SN.PLC.Build", "MQTT_SN.PLC.Run", "MQTT_SN.PLC.Start", "MQTT_SN.PLC.Stop" }));
      }
    }

    /// <summary>Каталог - только выходы this.X: Q даёт тип. Пишется, только если изменился.</summary>
    [Test]
    public void SyncFunc_WritesTheOutputsOfTheSource() {
      var f = Func("F", "this.M = A;\nreturn this.M;");
      Tick();
      Assert.That(LP_Schema.SyncFunc(f, null), Is.True);
      Tick();
      using (Assert.EnterMultipleScope()) {
        Assert.That(Keys(f.GetField(LP_Schema.OUT_MI)), Is.EqualTo(new[] { "M" }));
        Assert.That(f.GetField(LP_Schema.OUT_MI + ".M.default").ValueType, Is.EqualTo(JSC.JSValueType.Boolean));
        Assert.That((bool)f.GetField(LP_Schema.OUT_MI + ".M.default"), Is.True);
        Assert.That(LP_Schema.SyncFunc(f, null), Is.False, "не изменился");
      }
    }

    [Test]
    public void SyncFunc_KeepsTheCatalogWhileTheSourceIsBroken() {
      var f = Func("F", "this.M = A;");
      Tick();
      LP_Schema.SyncFunc(f, null);
      Tick();
      f.SetState(Str("this.M = ;"));
      Tick();
      using (Assert.EnterMultipleScope()) {
        Assert.That(LP_Schema.SyncFunc(f, null), Is.False);
        Assert.That(Keys(f.GetField(LP_Schema.OUT_MI)), Is.EqualTo(new[] { "M" }));
      }
    }

    [Test]
    public void SyncFunc_RemovesTheCatalogWithoutFields() {
      var f = Func("F", "this.M = A;");
      Tick();
      LP_Schema.SyncFunc(f, null);
      Tick();
      f.SetState(Str("return A;"));
      Tick();
      Assert.That(LP_Schema.SyncFunc(f, null), Is.True);
      Tick();
      Assert.That(f.GetField(LP_Schema.OUT_MI).IsObject(), Is.False);
    }

    /// <summary>Наблюдатель: каталог Func следует за src, меню переменных устройства есть без узла.</summary>
    [Test]
    public void Watcher_FollowsTheSource() {
      var f = Func("F", "this.M = A;");
      Tick();
      using (var w = new LP_Schema.Watcher(root)) {
        Ticks();
        using (Assert.EnterMultipleScope()) {
          Assert.That(Keys(f.GetField(LP_Schema.OUT_MI)), Is.EqualTo(new[] { "M" }), "уже существующий блок");
          Assert.That(_dev.GetField("Children").AsString("Float.menu", null), Is.EqualTo("LPLC"), "меню переменных");
        }
        f.SetState(Str("this.M = A;\nthis.N = B;"));
        var g = Func("G", "this.S = 1;");
        Ticks();
        using (Assert.EnterMultipleScope()) {
          Assert.That(Keys(f.GetField(LP_Schema.OUT_MI)), Is.EqualTo(new[] { "M", "N" }), "правка src");
          Assert.That(Keys(g.GetField(LP_Schema.OUT_MI)), Is.EqualTo(new[] { "S" }), "новый блок");
        }
      }
      f.SetState(Str("return A;"));
      Ticks();
      Assert.That(Keys(f.GetField(LP_Schema.OUT_MI)), Is.EqualTo(new[] { "M", "N" }), "после Dispose");
    }

    [Test]
    public void Watcher_IgnoresATopicThatIsNotPa1() {
      var other = root.Get("/dev/node2/pa1", true);
      var f = other.Get("F", true);
      f.SetField("type", LP_Tree.FUNC);
      f.SetState(Str("this.M = A;"));
      Tick();
      using (new LP_Schema.Watcher(root)) {
        Ticks();
      }
      Assert.That(f.GetField(LP_Schema.OUT_MI).Defined, Is.False);
    }

    /// <summary>Новый блок показывает пины из таблицы с числовыми константами, а не пустыми строками.</summary>
    /// <remarks>default входа в каталоге - число: редактор поля в Inspector выбирается по типу default, и со строкой
    /// константу не ввести. У Div и fDiv B - 1, как в шаблоне. Иконка - svg в самом LPLC.xst.</remarks>
    [TestCase("Func", "{'A':0}", "{'Q':true}")]
    [TestCase("Binary/bNOT", "{'A':0}", "{'Q':true}")]
    [TestCase("Binary/Trigger", "{'S':0,'D':0,'C':0,'R':0}", "{'Q':true,'NQ':true}")]
    [TestCase("Logic/lNOT", "{'A':0}", "{'Q':true}")]
    [TestCase("Math/Div", "{'A':0,'B':1}", "{'Q':true}")]
    [TestCase("Math/fDiv", "{'A':0,'B':1}", "{'Q':true}")]
    [TestCase("Math/ADD", "{'A':0,'B':0}", "{'Q':true}")]
    [TestCase("Logic/lOR", "{'A':0,'B':0}", "{'Q':true}")]
    [TestCase("Logic/COMP_LT", "{'A':0,'B':0}", "{'Q':true}")]
    [TestCase("Logic/COMP_LE", "{'A':0,'B':0}", "{'Q':true}")]
    [TestCase("Logic/COMP_EQ", "{'A':0,'B':0}", "{'Q':true}")]
    [TestCase("Logic/COMP_NE", "{'A':0,'B':0}", "{'Q':true}")]
    [TestCase("Logic/COMP_GT", "{'A':0,'B':0}", "{'Q':true}")]
    [TestCase("Logic/COMP_GE", "{'A':0,'B':0}", "{'Q':true}")]
    [TestCase("TWI/SCAN_BUS", "{'Scan':0}", "{'Addr':true}")]
    [TestCase("Pin", "{'x':0}", "{'value':true}")]
    public void Types_DeclareShownAndPossiblePins(string type, string shownIn, string shownOut) {
      ImportTypes();
      Tick();
      var s = root.Get("/$YS/TYPES/LPLC/" + type, false).GetState();
      var mi = s.Field("mi.LPLC.mi");
      var shown = Json(shownIn);
      using (Assert.EnterMultipleScope()) {
        Assert.That(JsLib.Stringify(s.Field("manifest.LPLC.in")), Is.EqualTo(JsLib.Stringify(shown)));
        Assert.That(JsLib.Stringify(s.Field("manifest.LPLC.out")), Is.EqualTo(JsLib.Stringify(Json(shownOut))));
        foreach (var kv in mi.Field("in.mi")) {
          var c = shown.Field(kv.Key);
          Assert.That(kv.Value.Field("default").ValueType, Is.EqualTo(JSC.JSValueType.Integer), kv.Key);
          Assert.That(kv.Value.AsInt("default", -1), Is.EqualTo(c.ValueType == JSC.JSValueType.Integer ? (int)c : 0), kv.Key);
        }
        Assert.That(Keys(mi.Field("out.mi")), Is.EqualTo(Keys(Json(shownOut))));
        Assert.That(s.AsString("icon", null), Does.StartWith("data:image/svg+xml;base64,"));
      }
    }

    [Test]
    public void AllTemplateInputsAreNumericAndOldInstancesArePreserved() {
      var old = Func("Old", "return A;");
      var type = root.Get("/$YS/TYPES/LPLC/Math/ADD", true);
      type.SetState(Json("{'manifest':{'LPLC':{'in':{'A':'','B':''}}}}"));
      type.SetField("version", "¤VR0.5.2610.23001");
      var trigger = root.Get("/$YS/TYPES/LPLC/Binary/Trigger", true);
      trigger.SetState(Json("{'LPLC':{'src':'return S;'}}"));
      trigger.SetField("version", "¤VR0.5.2610.27001");
      Tick();
      ImportTypes();
      Tick();
      var templates = root.Get("/$YS/TYPES/LPLC", false).all.Where(t => t.GetState().Field("manifest.LPLC").IsObject()).ToArray();
      Assert.That(templates, Has.Length.EqualTo(24));
      foreach(var template in templates) {
        foreach(var pin in template.GetState().Field("manifest.LPLC.in")) {
          Assert.That(pin.Value.IsNumber, Is.True, template.path + "." + pin.Key);
        }
        string version = template.name == "SerialPort" || template.name == "SCAN_BUS" ? "¤VR0.5.2611.3003" : template == trigger ? "¤VR0.5.2610.27002"
          : template.name == "TwiMaster" ? "¤VR0.5.2610.27005" : "¤VR0.5.2610.27001";
        Assert.That(template.GetField("version").AsString(null), Is.EqualTo(version));
      }
      Assert.That(trigger.GetState().Field("LPLC.src").AsString(null), Does.Contain("constructor()"));
      Assert.That(type.GetState().Field("manifest.LPLC.in.A").AsInt(-1), Is.Zero);
      Assert.That(old.GetField("LPLC.in.A").AsString(null), Is.Empty);
      Assert.That(old.GetState().AsString(null), Is.EqualTo("return A;"));
      Assert.That(root.Get("/$YS/TYPES/LPLC/Func", false).GetState().AsString("default", null), Is.Empty);
    }

    /// <summary>Inspector и редактор видят один каталог: тип и собственный манифест блока вместе.</summary>
    [Test]
    public void Catalog_MergesTheTypeAndTheBlock() {
      ImportTypes();
      var f = Func("F", "this.M = A;");
      Tick();
      LP_Schema.SyncFunc(f, null);
      Tick();
      var overlay = X13.WebUI.SchemaOverlay.ForManifest(f);
      using (Assert.EnterMultipleScope()) {
        Assert.That(overlay.At("LPLC.out").CatalogEntries.Select(z => z.Key), Is.EqualTo(new[] { "M", "Q" }));
        Assert.That(overlay.At("LPLC.in").CatalogEntries.Select(z => z.Key), Is.EqualTo(new[] { "A", "B", "C", "D", "E", "F", "G", "H" }));
      }
    }
  }
}
