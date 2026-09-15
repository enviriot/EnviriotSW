///<remarks>Этот файл является частью проекта <see cref="https://github.com/enviriot">Enviriot</see>.<remarks>
using System;
using NiL.JS.Extensions;
using NUnit.Framework;
using X13.Repository;
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;

namespace X13.Tests {
  /// <summary>Содержимое топика: состояние, манифест и атрибуты.</summary>
  /// <remarks>В отличие от структуры всё это откладывается до тика целиком - и запись, и событие.
  /// Поэтому почти каждый тест здесь проверяет обе точки: что до тика ничего не изменилось и что
  /// после тика изменилось ровно то, что просили.</remarks>
  [TestFixture]
  public class TopicStateFieldTests : RepoTestBase {

    #region State

    [Test]
    public void GetState_OfANewTopicIsUndefined() {
      Topic t = root.Get("/a");
      Assert.That(t.GetState().ValueType, Is.EqualTo(JSC.JSValueType.Undefined));
      Assert.That(t.GetState().Defined, Is.False);
    }

    [Test]
    public void SetState_IsAppliedOnTheTick() {
      Topic t = root.Get("/a");
      Tick();
      ClearEvents();
      t.SetState(Num(42));
      Assert.That(t.GetState().Defined, Is.False);
      Tick();
      Assert.That((int)t.GetState(), Is.EqualTo(42));
      Assert.That(KindsOf(t), Is.EqualTo(new[] { EventKind.StateChanged }));
    }

    /// <summary>Датчик, обновляющийся чаще тика, порождает одно событие с последним значением.</summary>
    [Test]
    public void SetState_TwiceInOneTickKeepsTheLastValue() {
      Topic t = root.Get("/a");
      Tick();
      ClearEvents();
      t.SetState(Num(1));
      t.SetState(Num(2));
      t.SetState(Num(3));
      Tick();
      Assert.That((int)t.GetState(), Is.EqualTo(3));
      Assert.That(KindsOf(t), Is.EqualTo(new[] { EventKind.StateChanged }));
    }

    /// <summary>Событие несёт прежнее значение - подписчику нужно и оно.</summary>
    [Test]
    public void SetState_ReportsThePreviousValue() {
      Topic t = root.Get("/a");
      t.SetState(Num(1));
      Tick();
      ClearEvents();
      t.SetState(Num(2));
      Tick();
      TopicEvent e = EventOf(t, EventKind.StateChanged);
      Assert.That(e, Is.Not.Null);
      Assert.That((int)e.OldState, Is.EqualTo(1));
    }

    [Test]
    public void SetState_RepeatedValueIsNotAnEvent() {
      Topic t = root.Get("/a");
      t.SetState(Num(1));
      Tick();
      ClearEvents();
      t.SetState(Num(1));   // равное по значению, но другой экземпляр
      Tick();
      Assert.That(KindsOf(t), Is.Empty);
    }

    /// <summary>Тот же самый экземпляр - не изменение, и события не порождает.</summary>
    [Test]
    public void SetState_TheVerySameInstanceIsNotAChange() {
      Topic t = root.Get("/a");
      JSC.JSValue v = Num(1);
      t.SetState(v);
      Tick();
      ClearEvents();
      t.SetState(v);
      Tick();
      Assert.That(KindsOf(t), Is.Empty);
    }

    #endregion State

    #region JsValueTypeName

    [Test]
    public void JsValueTypeName_OfNullReferenceIsNull() {
      Assert.That(Topic.JsValueTypeName(null), Is.Null);
    }

    [Test]
    public void JsValueTypeName_IntegerAndDoubleAreBothDouble() {
      // Дерево не различает целое и дробное: у топика один числовой тип.
      Assert.That(Topic.JsValueTypeName(Num(1)), Is.EqualTo("Double"));
      Assert.That(Topic.JsValueTypeName(Num(1.5)), Is.EqualTo("Double"));
    }

    [Test]
    public void JsValueTypeName_String() {
      Assert.That(Topic.JsValueTypeName(Str("abc")), Is.EqualTo("String"));
    }

    /// <summary>Версия - строка с меткой: по ней Xst решает, импортировать документ или нет.</summary>
    [Test]
    public void JsValueTypeName_MarkedStringIsAVersion() {
      Assert.That(Topic.JsValueTypeName(Str("¤VR0.5.2609.4003")), Is.EqualTo("Version"));
    }

    [Test]
    public void JsValueTypeName_Boolean() {
      Assert.That(Topic.JsValueTypeName(new JSL.Boolean(true)), Is.EqualTo("Boolean"));
    }

    [Test]
    public void JsValueTypeName_NullAndObjectAreToldApart() {
      Assert.That(Topic.JsValueTypeName(JSC.JSValue.Null), Is.EqualTo("Null"));
      Assert.That(Topic.JsValueTypeName(JSC.JSObject.CreateObject()), Is.EqualTo("Object"));
    }

    [Test]
    public void JsValueTypeName_UndefinedHasNoName() {
      Assert.That(Topic.JsValueTypeName(JSC.JSValue.Undefined), Is.Null);
    }

    [Test]
    public void GetStateType_ReadsTheTopicsOwnState() {
      Topic t = root.Get("/a");
      Assert.That(Topic.JsValueTypeName(t.GetState()), Is.Null);   // Undefined
      t.SetState(Str("on"));
      Tick();
      Assert.That(Topic.JsValueTypeName(t.GetState()), Is.EqualTo("String"));
    }

    #endregion JsValueTypeName

    #region Fields

    /// <summary>Пустой путь поля - это весь манифест, а не отсутствующее поле.</summary>
    [Test]
    public void GetField_WithoutAPathIsTheWholeManifest() {
      Topic t = root.Get("/a");
      JSC.JSValue m = t.GetField(null);
      Assert.That(m.Defined, Is.True);
      Assert.That(m["attr"].IsNumber, Is.True);
      Assert.That(t.GetField(string.Empty), Is.SameAs(m));
    }

    [Test]
    public void GetField_OfAMissingFieldIsUndefined() {
      Topic t = root.Get("/a");
      Assert.That(t.GetField("nope").Defined, Is.False);
      Assert.That(t.GetField("nope.deeper").Defined, Is.False);
    }

    /// <summary>Точка разделяет сегменты пути поля, а пустые сегменты отбрасываются.</summary>
    [Test]
    public void GetField_DropsEmptySegments() {
      Topic t = root.Get("/a");
      Assert.That((int)t.GetField(".attr."), Is.EqualTo(0));
      Assert.That((int)t.GetField("..attr"), Is.EqualTo(0));
    }

    [Test]
    public void SetField_IsAppliedOnTheTick() {
      Topic t = root.Get("/a");
      Tick();
      ClearEvents();
      t.SetField("hint", Str("lamp"));
      Assert.That(t.GetField("hint").Defined, Is.False);
      Tick();
      Assert.That(t.GetField("hint").Value, Is.EqualTo("lamp"));
      Assert.That(KindsOf(t), Is.EqualTo(new[] { EventKind.FieldChanged }));
    }

    [Test]
    public void SetField_CreatesTheIntermediateObjects() {
      Topic t = root.Get("/a");
      t.SetField("MQTT.uri", Str("mqtt://localhost"));
      Tick();
      Assert.That(t.GetField("MQTT").IsObject(), Is.True);
      Assert.That(t.GetField("MQTT.uri").Value, Is.EqualTo("mqtt://localhost"));
    }

    /// <summary>Манифест заменяется целиком, а не правится на месте.</summary>
    /// <remarks>На этом держится сравнение в TopicEvent: подписчик получает манифест, каким тот
    /// был до пакета, и он должен остаться прежним, а не измениться заодно с новым.</remarks>
    [Test]
    public void SetField_ReplacesTheManifestInsteadOfMutatingIt() {
      Topic t = root.Get("/a");
      Tick();
      JSC.JSValue before = t.GetField(null);
      t.SetField("hint", Str("lamp"));
      Tick();
      Assert.That(t.GetField(null), Is.Not.SameAs(before));
      Assert.That(before["hint"].Defined, Is.False);
    }

    /// <summary>Две записи в одно поле за тик объединяются: побеждает последняя.</summary>
    [Test]
    public void SetField_TwiceIntoOneFieldKeepsTheLastValue() {
      Topic t = root.Get("/a");
      Tick();
      ClearEvents();
      t.SetField("hint", Str("first"));
      t.SetField("hint", Str("second"));
      Tick();
      Assert.That(t.GetField("hint").Value, Is.EqualTo("second"));
      Assert.That(KindsOf(t), Is.EqualTo(new[] { EventKind.FieldChanged }));
    }

    /// <summary>Записи в разные поля за один тик складываются в один манифест и разные события.</summary>
    /// <remarks>Раньше события тоже сворачивались в одно, и оно несло путь той записи, что пришла
    /// первой. Потребитель, сверяющий FieldPath с именем своего поля, терял его всякий раз, когда
    /// кто-то другой успевал записать раньше.</remarks>
    [Test]
    public void SetField_IntoDifferentFieldsGivesOneEventEach() {
      Topic t = root.Get("/a");
      Tick();
      ClearEvents();
      t.SetField("hint", Str("lamp"));
      t.SetField("type", Str("Bool"));
      Tick();
      Assert.That(t.GetField("hint").Value, Is.EqualTo("lamp"));
      Assert.That(t.GetField("type").Value, Is.EqualTo("Bool"));
      Assert.That(KindsOf(t), Is.EqualTo(new[] { EventKind.FieldChanged, EventKind.FieldChanged }));
      Assert.That(events.ConvertAll(e => e.FieldPath), Is.EqualTo(new[] { "hint", "type" }));
    }

    /// <summary>Запись того же значения ничего не меняет и события не порождает.</summary>
    /// <remarks>Здесь сравнение по значению, в отличие от состояния: манифест - это метаданные,
    /// а не отсчёт, и повторная запись того же в него не событие.</remarks>
    [Test]
    public void SetField_TheSameValueIsNotAChange() {
      Topic t = root.Get("/a");
      t.SetField("hint", Str("lamp"));
      Tick();
      ClearEvents();
      t.SetField("hint", Str("lamp"));
      Tick();
      Assert.That(KindsOf(t), Is.Empty);
    }

    [Test]
    public void SetField_EmptyPathIsABadArgument() {
      Topic t = root.Get("/a");
      Action act = () => t.SetField(string.Empty, Str("x"));
      Assert.That(act, Throws.ArgumentException);
    }

    /// <summary>Try-метод отвечает, а не бросает - и ничего при этом не ставит в очередь.</summary>
    [Test]
    public void TrySetField_AnswersFalseForAnEmptyPath() {
      Topic t = root.Get("/a");
      Tick();
      ClearEvents();
      Assert.That(t.TrySetField(string.Empty, Str("x"), null), Is.False);
      Assert.That(t.TrySetField(null, Str("x"), null), Is.False);
      Tick();
      Assert.That(events, Is.Empty);
    }

    [Test]
    public void TrySetField_AnswersTrueAndWrites() {
      Topic t = root.Get("/a");
      Assert.That(t.TrySetField("hint", Str("lamp"), null), Is.True);
      Tick();
      Assert.That(t.GetField("hint").Value, Is.EqualTo("lamp"));
    }

    #endregion Fields

    #region Attributes

    [Test]
    public void CheckAttribute_OfAFreshTopicIsFalse() {
      Topic t = root.Get("/a");
      Assert.That(t.CheckAttribute(Topic.Attribute.Required), Is.False);
      Assert.That(t.CheckAttribute(Topic.Attribute.Readonly), Is.False);
      Assert.That(t.CheckAttribute(Topic.Attribute.Config), Is.False);
    }

    /// <summary>Без манифеста читать нечего, и ответ - false, а не исключение.</summary>
    [Test]
    public void CheckAttribute_WithoutAManifestIsFalse() {
      Topic t = Topic.Declare(root, "/a");
      Assert.That(t.CheckAttribute(Topic.Attribute.Required), Is.False);
    }

    [Test]
    public void SetAttribute_IsAppliedOnTheTick() {
      Topic t = root.Get("/a");
      t.SetAttribute(Topic.Attribute.Required);
      Assert.That(t.CheckAttribute(Topic.Attribute.Required), Is.False);
      Tick();
      Assert.That(t.CheckAttribute(Topic.Attribute.Required), Is.True);
    }

    /// <summary>Атрибуты добавляются к уже имеющимся, а не заменяют их.</summary>
    [Test]
    public void SetAttribute_AddsToWhatIsAlreadyThere() {
      Topic t = root.Get("/a");
      t.SetAttribute(Topic.Attribute.Required);
      Tick();
      t.SetAttribute(Topic.Attribute.Readonly);
      Tick();
      Assert.That(t.CheckAttribute(Topic.Attribute.Required), Is.True);
      Assert.That(t.CheckAttribute(Topic.Attribute.Readonly), Is.True);
    }

    /// <summary>DB и Config взаимно исключают друг друга: второй вытесняет первый.</summary>
    /// <remarks>Проверяется бит, а не всё значение целиком - настоящие вызывающие передают
    /// объединённые флаги вида Required|Readonly|Config.</remarks>
    [Test]
    public void SetAttribute_DbAndConfigAreMutuallyExclusive() {
      Topic t = root.Get("/a");
      t.SetAttribute(Topic.Attribute.Required | Topic.Attribute.Config);
      Tick();
      Assert.That(t.CheckAttribute(Topic.Attribute.Saved, Topic.Attribute.Config), Is.True);
      t.SetAttribute(Topic.Attribute.DB);
      Tick();
      Assert.That(t.CheckAttribute(Topic.Attribute.Saved, Topic.Attribute.DB), Is.True);
      Assert.That(t.CheckAttribute(Topic.Attribute.Saved, Topic.Attribute.Config), Is.False);
      Assert.That(t.CheckAttribute(Topic.Attribute.Required), Is.True);   // остальное на месте
    }

    [Test]
    public void ClearAttribute_RemovesOnlyWhatItWasAsked() {
      Topic t = root.Get("/a");
      t.SetAttribute(Topic.Attribute.Required | Topic.Attribute.Readonly);
      Tick();
      t.ClearAttribute(Topic.Attribute.Readonly);
      Tick();
      Assert.That(t.CheckAttribute(Topic.Attribute.Readonly), Is.False);
      Assert.That(t.CheckAttribute(Topic.Attribute.Required), Is.True);
    }

    /// <summary>Два аргумента CheckAttribute - это маска и ожидаемое значение под ней.</summary>
    [Test]
    public void CheckAttribute_MatchesTheValueUnderTheMask() {
      Topic t = root.Get("/a");
      t.SetAttribute(Topic.Attribute.Config);
      Tick();
      Assert.That(t.CheckAttribute(Topic.Attribute.Saved, Topic.Attribute.Config), Is.True);
      Assert.That(t.CheckAttribute(Topic.Attribute.Saved, Topic.Attribute.DB), Is.False);
      // None во втором аргументе означает "то же, что и один аргумент": маска сравнивается сама с собой.
      Assert.That(t.CheckAttribute(Topic.Attribute.Saved, Topic.Attribute.None), Is.False);
    }

    /// <summary>Атрибут - это поле манифеста, и записывается тем же путём.</summary>
    [Test]
    public void SetAttribute_WritesTheAttrField() {
      Topic t = root.Get("/a");
      t.SetAttribute(Topic.Attribute.Required | Topic.Attribute.Internal);
      Tick();
      Assert.That((int)t.GetField("attr"), Is.EqualTo(65));
      Assert.That(EventOf(t, EventKind.FieldChanged).FieldPath, Is.EqualTo("attr"));
    }

    #endregion Attributes
  }
}
