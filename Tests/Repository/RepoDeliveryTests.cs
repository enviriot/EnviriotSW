///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using System.Collections.Generic;
using NUnit.Framework;
using X13.Repository;
using JSC = NiL.JS.Core;

namespace X13.Tests {
  /// <summary>Фильтр доставки: какая из уже существующих подписок вызывается для опубликованного события.</summary>
  /// <remarks>Реестр SubscribeAll, которым пользуется обвязка, для этого непригоден: он получает всё,
  /// а маски и префикс действуют в Topic.Deliver и видны только через обработчик отдельной подписки.
  /// Соседний фильтр - отбор топиков для снимка - работает в Repo.Snapshot, то есть до публикации,
  /// и проверяется в RepoSubscriptionTests; здесь подписка всегда создаётся заранее, а её снимок
  /// отбрасывается до начала проверяемых записей.</remarks>
  [TestFixture]
  public class RepoDeliveryTests : RepoTestBase {

    /// <summary>Подписка, чей снимок уже получен и отброшен: дальше в список попадают только изменения.</summary>
    private List<TopicEvent> Watch(Topic t, SubRec.SubMask mask, string prefix) {
      List<TopicEvent> got = new List<TopicEvent>();
      t.Subscribe(mask, prefix, (e, sr) => got.Add(e));
      Tick();
      got.Clear();
      return got;
    }

    /// <summary>Изменение состояния получает подписка с битом Value, и только она.</summary>
    [Test]
    public void Deliver_AStateChangeNeedsTheValueBit() {
      Topic t = root.Get("/a");
      Tick();
      List<TopicEvent> value = Watch(t, SubRec.SubMask.Once | SubRec.SubMask.Value, null);
      List<TopicEvent> field = Watch(t, SubRec.SubMask.Once | SubRec.SubMask.Field, null);
      t.SetState(Num(1));
      Tick();
      Assert.That(Kinds(value), Is.EqualTo(new[] { EventKind.StateChanged }));
      Assert.That(field.Count, Is.EqualTo(0));
    }

    /// <summary>Запись в манифест получает подписка с битом Field, и только она.</summary>
    [Test]
    public void Deliver_AFieldChangeNeedsTheFieldBit() {
      Topic t = root.Get("/a");
      Tick();
      List<TopicEvent> value = Watch(t, SubRec.SubMask.Once | SubRec.SubMask.Value, null);
      List<TopicEvent> field = Watch(t, SubRec.SubMask.Once | SubRec.SubMask.Field, null);
      t.SetField("hint", Str("lamp"));
      Tick();
      Assert.That(Kinds(field), Is.EqualTo(new[] { EventKind.FieldChanged }));
      Assert.That(value.Count, Is.EqualTo(0));
    }

    /// <summary>Запись внутри префикса - обычный случай - доходит, сообщая собственный путь.</summary>
    [Test]
    public void Deliver_APrefixTakesAWriteInsideIt() {
      Topic t = root.Get("/a");
      Tick();
      List<TopicEvent> got = Watch(t, SubRec.SubMask.Once | SubRec.SubMask.Field, "MQTT");
      t.SetField("MQTT.uri", Str("mqtt://localhost"));
      Tick();
      Assert.That(Kinds(got), Is.EqualTo(new[] { EventKind.FieldChanged }));
      Assert.That(got[0].FieldPath, Is.EqualTo("MQTT.uri"));
    }

    /// <summary>Запись в соседнюю ветку не доходит, хотя манифест изменился целиком.</summary>
    /// <remarks>Обе записи попадают в один пакет и делят один манифест, поэтому сравнение манифестов
    /// одинаково для обоих событий: отличить их может только собственный путь каждого события.
    /// Проверка - число вызовов, а не факт вызова: подписчик одного поля, вызываемый на каждую запись
    /// в топик, выглядит работающим ровно до того дня, когда рядом появляется второе поле.</remarks>
    [Test]
    public void Deliver_APrefixSkipsAWriteInAnotherBranch() {
      Topic t = root.Get("/a");
      Tick();
      List<TopicEvent> got = Watch(t, SubRec.SubMask.Once | SubRec.SubMask.Field, "MQTT");
      t.SetField("MQTT.uri", Str("mqtt://localhost"));
      t.SetField("hint", Str("lamp"));
      Tick();
      Assert.That(Kinds(got), Is.EqualTo(new[] { EventKind.FieldChanged }));
      Assert.That(got[0].FieldPath, Is.EqualTo("MQTT.uri"));
    }

    /// <summary>Ветка сравнивается посегментно: "MQTT-SNx" не находится внутри "MQTT-SN".</summary>
    [Test]
    public void Deliver_APrefixIsComparedBySegments() {
      Topic t = root.Get("/a");
      Tick();
      List<TopicEvent> got = Watch(t, SubRec.SubMask.Once | SubRec.SubMask.Field, "MQTT-SN");
      t.SetField("MQTT-SNx.model", Str("XYZ"));
      Tick();
      Assert.That(got.Count, Is.EqualTo(0));
    }

    /// <summary>Запись, заменяющая ветку целиком, доходит до подписчика поля внутри неё.</summary>
    /// <remarks>Направление, которое легко упустить: путь записи короче префикса, но поле подписчика
    /// изменено именно ею.</remarks>
    [Test]
    public void Deliver_AWriteOverTheWholeBranchReachesAPrefixInsideIt() {
      Topic t = root.Get("/a");
      t.SetField("MQTT.uri", Str("mqtt://localhost"));
      Tick();
      List<TopicEvent> got = Watch(t, SubRec.SubMask.Once | SubRec.SubMask.Field, "MQTT.uri");
      JSC.JSValue mqtt = JSC.JSObject.CreateObject();
      mqtt["uri"] = Str("mqtt://other");
      t.SetField("MQTT", mqtt);
      Tick();
      Assert.That(Kinds(got), Is.EqualTo(new[] { EventKind.FieldChanged }));
      Assert.That(got[0].FieldPath, Is.EqualTo("MQTT"));
    }

    /// <summary>Запись в ветку подписчика, не тронувшая его поле, до него не доходит.</summary>
    /// <remarks>Это работа сравнения манифестов: путь записи ("MQTT") накрывает префикс ("MQTT.uri"),
    /// то есть проверки ветки недостаточно, а само поле осталось прежним.</remarks>
    [Test]
    public void Deliver_AWriteThatLeavesThePrefixAloneDoesNotReachIt() {
      Topic t = root.Get("/a");
      JSC.JSValue mqtt = JSC.JSObject.CreateObject();
      mqtt["uri"] = Str("mqtt://localhost");
      mqtt["qos"] = Num(0);
      t.SetField("MQTT", mqtt);
      Tick();
      List<TopicEvent> got = Watch(t, SubRec.SubMask.Once | SubRec.SubMask.Field, "MQTT.uri");
      JSC.JSValue next = JSC.JSObject.CreateObject();
      next["uri"] = t.GetField("MQTT.uri");
      next["qos"] = Num(1);
      t.SetField("MQTT", next);
      Tick();
      Assert.That(got.Count, Is.EqualTo(0));
    }

    /// <summary>Children - ровно один уровень вниз: ни внук, ни сам топик подписки не считаются.</summary>
    /// <remarks>Три случая проверяются вместе, потому что рознят их не маска и не событие, а только
    /// уровень, на котором подписка встречается при подъёме от источника. Записи топика обрабатывают
    /// Once и All, записи родителя - Children и All, записи прочих предков - только All.</remarks>
    [Test]
    public void Deliver_ChildrenIsOneLevelOnly() {
      Topic a = root.Get("/a");
      Topic b = root.Get("/a/b");
      Topic c = root.Get("/a/b/c");
      Tick();
      List<TopicEvent> got = Watch(a, SubRec.SubMask.Children | SubRec.SubMask.Value, null);

      b.SetState(Num(1));
      c.SetState(Num(2));
      a.SetState(Num(3));
      Tick();
      Assert.That(Kinds(got), Is.EqualTo(new[] { EventKind.StateChanged }));
      Assert.That(got[0].Source, Is.SameAs(b));
    }

    /// <summary>All - всё поддерево на любой глубине, включая сам топик подписки.</summary>
    [Test]
    public void Deliver_AllReachesAnyDepth() {
      Topic a = root.Get("/a");
      Topic b = root.Get("/a/b");
      Topic c = root.Get("/a/b/c");
      Tick();
      List<TopicEvent> got = Watch(a, SubRec.SubMask.All | SubRec.SubMask.Value, null);

      a.SetState(Num(1));
      b.SetState(Num(2));
      c.SetState(Num(3));
      Tick();
      Assert.That(Kinds(got), Is.EqualTo(new[] { EventKind.StateChanged, EventKind.StateChanged, EventKind.StateChanged }));
      Assert.That(got[0].Source, Is.SameAs(a));
      Assert.That(got[1].Source, Is.SameAs(b));
      Assert.That(got[2].Source, Is.SameAs(c));
    }

    /// <summary>Once - только сам топик: изменение потомка до него не доходит.</summary>
    [Test]
    public void Deliver_OnceDoesNotReachTheChildren() {
      Topic a = root.Get("/a");
      Topic b = root.Get("/a/b");
      Tick();
      List<TopicEvent> got = Watch(a, SubRec.SubMask.Once | SubRec.SubMask.Value, null);

      b.SetState(Num(1));
      Tick();
      Assert.That(got.Count, Is.EqualTo(0));

      a.SetState(Num(2));
      Tick();
      Assert.That(Kinds(got), Is.EqualTo(new[] { EventKind.StateChanged }));
      Assert.That(got[0].Source, Is.SameAs(a));
    }

    /// <summary>Маска с обоими битами сразу вызывается один раз, а не дважды.</summary>
    /// <remarks>Прямой потомок - тот самый случай, ради которого уровни разделены: он подходит
    /// и под Children, и под All, и родитель обходится ровно один раз, со сложенной областью.
    /// Внук проверяется рядом, чтобы видеть, что бит All при этом не потерян.</remarks>
    [Test]
    public void Deliver_ChildrenAndAllTogetherAreInvokedOnce() {
      Topic a = root.Get("/a");
      Topic b = root.Get("/a/b");
      Topic c = root.Get("/a/b/c");
      Tick();
      List<TopicEvent> got = Watch(a, SubRec.SubMask.Children | SubRec.SubMask.All | SubRec.SubMask.Value, null);

      b.SetState(Num(1));
      c.SetState(Num(2));
      Tick();
      Assert.That(Kinds(got), Is.EqualTo(new[] { EventKind.StateChanged, EventKind.StateChanged }));
      Assert.That(got[0].Source, Is.SameAs(b));
      Assert.That(got[1].Source, Is.SameAs(c));
    }

    /// <summary>Создание, перемещение и удаление доходят до подписки, не назвавшей ни Value, ни Field.</summary>
    /// <remarks>Фильтры по битам отсекают только StateChanged и FieldChanged - у структурного события
    /// собственного бита нет, и назвать его в маске нельзя. Подписчик поля узнаёт о появлении и
    /// исчезновении топиков в своей области независимо от того, просил он об этом или нет.</remarks>
    [Test]
    public void Deliver_AStructuralEventNeedsNoValueOrFieldBit() {
      Topic a = root.Get("/a");
      Tick();
      List<TopicEvent> got = Watch(a, SubRec.SubMask.Children | SubRec.SubMask.Field, null);

      Topic b = root.Get("/a/b");
      Tick();
      b.Move(a, "b2");
      Tick();
      b.Remove();
      Tick();
      Assert.That(Kinds(got), Is.EqualTo(new[] { EventKind.Created, EventKind.Moved, EventKind.Removed }));
    }

    /// <summary>Удаление поддерева доходит до подписчика-предка, хотя топики уже отсоединены.</summary>
    /// <remarks>Держится на том, что Unlink снимает топик с массива детей родителя, но не обнуляет
    /// ссылку на родителя. Публикация идёт после всех Apply, то есть после отсоединения: обнули её -
    /// и подъём от источника не найдёт ни одного предка, а об удалении узнает только тот, кто подписан
    /// на сам удаляемый топик. Проверка целости дерева стоит здесь же, иначе тест прошёл бы
    /// и в случае, когда удаление вообще не состоялось.</remarks>
    [Test]
    public void Deliver_RemovalOfASubtreeReachesTheAncestor() {
      Topic a = root.Get("/a");
      Topic b = root.Get("/a/b");
      Topic c = root.Get("/a/b/c");
      Tick();
      List<TopicEvent> got = Watch(a, SubRec.SubMask.All | SubRec.SubMask.Value, null);

      b.Remove();
      Tick();
      Assert.That(Kinds(got), Is.EqualTo(new[] { EventKind.Removed, EventKind.Removed }));
      Assert.That(got[0].Source, Is.SameAs(b));
      Assert.That(got[1].Source, Is.SameAs(c));
      Assert.That(a.Exist("b"), Is.False);
    }

    /// <summary>Перемещение объявляется обоим родителям: и месту назначения, и месту ухода.</summary>
    /// <remarks>Структура меняется сразу, а публикация идёт тиком позже, поэтому обычный подъём
    /// знает только новую цепочку. Ради этого случая событие несёт прежнего родителя ссылкой:
    /// следящий за составом своих детей иначе оставался бы с потомком, которого там больше нет,
    /// и починить это сам не мог бы - события просто не было. Обоим приходит одно и то же событие,
    /// в котором Source указывает на новое место, а OldPath - на прежнее.</remarks>
    [Test]
    public void Deliver_AMoveIsAnnouncedUnderBothParents() {
      Topic src = root.Get("/src");
      Topic dst = root.Get("/dst");
      Topic t = root.Get("/src/t");
      Tick();
      List<TopicEvent> atSrc = Watch(src, SubRec.SubMask.Children | SubRec.SubMask.Value, null);
      List<TopicEvent> atDst = Watch(dst, SubRec.SubMask.Children | SubRec.SubMask.Value, null);

      t.Move(dst, null);
      Tick();
      Assert.That(Kinds(atDst), Is.EqualTo(new[] { EventKind.Moved }));
      Assert.That(Kinds(atSrc), Is.EqualTo(new[] { EventKind.Moved }));
      Assert.That(atSrc[0].OldPath, Is.EqualTo("/src/t"));
      Assert.That(atSrc[0].Source, Is.SameAs(t));
      Assert.That(t.path, Is.EqualTo("/dst/t"));
    }

    /// <summary>Подписчик, накрывающий обе цепочки, вызывается один раз, а не дважды.</summary>
    /// <remarks>Главное следствие второго подъёма и его главный риск. Перемещение внутри одного
    /// поддерева - обычное дело (перетащить узел на соседнюю ветку), и подписка на общем предке
    /// подходит под обе цепочки. Обход покинутой цепочки обязан остановиться на первом общем
    /// предке, иначе такой подписчик получает одно перемещение дважды.</remarks>
    [Test]
    public void Deliver_AMoveInsideOneSubtreeIsAnnouncedOnce() {
      Topic a = root.Get("/a");
      Topic b = root.Get("/a/b");
      Topic c = root.Get("/a/c");
      Topic x = root.Get("/a/b/x");
      Tick();
      List<TopicEvent> got = Watch(a, SubRec.SubMask.All | SubRec.SubMask.Value, null);

      x.Move(c, null);
      Tick();
      Assert.That(Kinds(got), Is.EqualTo(new[] { EventKind.Moved }));
      Assert.That(got[0].Source, Is.SameAs(x));
      Assert.That(b.HasChildren(), Is.False);
    }

    /// <summary>Переименование объявляется один раз: родитель не менялся и второму обходу нечего делать.</summary>
    [Test]
    public void Deliver_ARenameIsAnnouncedOnce() {
      Topic a = root.Get("/a");
      Topic x = root.Get("/a/x");
      Tick();
      List<TopicEvent> got = Watch(a, SubRec.SubMask.Children | SubRec.SubMask.Value, null);

      x.Move(null, "y");
      Tick();
      Assert.That(Kinds(got), Is.EqualTo(new[] { EventKind.Moved }));
      Assert.That(got[0].OldPath, Is.EqualTo("/a/x"));
      Assert.That(x.path, Is.EqualTo("/a/y"));
    }

    /// <summary>В покинутой цепочке уровни те же: прежний родитель - Children, всё выше - только All.</summary>
    /// <remarks>Иначе второй подъём оказался бы щедрее первого: подписчик Children на прародителе
    /// узнавал бы об уходе внука, о приходе которого ему никто не сообщал бы.</remarks>
    [Test]
    public void Deliver_TheAbandonedChainKeepsTheLevels() {
      Topic a = root.Get("/a");
      Topic b = root.Get("/a/b");
      Topic x = root.Get("/a/b/x");
      Topic dst = root.Get("/dst");
      Tick();
      List<TopicEvent> atB = Watch(b, SubRec.SubMask.Children | SubRec.SubMask.Value, null);
      List<TopicEvent> atA = Watch(a, SubRec.SubMask.Children | SubRec.SubMask.Value, null);
      List<TopicEvent> atAll = Watch(a, SubRec.SubMask.All | SubRec.SubMask.Value, null);

      x.Move(dst, null);
      Tick();
      Assert.That(Kinds(atB), Is.EqualTo(new[] { EventKind.Moved }));
      Assert.That(Kinds(atAll), Is.EqualTo(new[] { EventKind.Moved }));
      Assert.That(atA.Count, Is.EqualTo(0));
    }
  }
}
