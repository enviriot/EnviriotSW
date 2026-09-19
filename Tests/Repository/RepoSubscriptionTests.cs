///<remarks>Этот файл является частью проекта <see cref="https://github.com/enviriot">Enviriot</see>.<remarks>
using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using X13.Repository;

namespace X13.Tests {
  /// <summary>Подписки со стороны репозитория: реестр SubscribeAll и снимок, выдаваемый новой подписке.</summary>
  /// <remarks>Здесь два разных инструмента, и путать их нельзя. Topic.Subscribe(Action&lt;TopicEvent&gt;)
  /// - это реестр всего репозитория: он получает каждое событие, включая Snapshot и Ready, адресованные
  /// чужой подписке, и маски с префиксом на него не действуют вовсе. Фильтрация доставки живёт
  /// в Topic.Deliver, поэтому видна только через обработчик отдельной подписки.
  /// <para>Фильтр же самого снимка работает в Repo.Snapshot, то есть до публикации, и относится
  /// именно к предмету этого файла.</para>
  /// <para>Подписчики обходятся с конца массива, а коллектор базы зарегистрирован первым - значит,
  /// он вызывается последним, после любой подписки, добавленной тестом. Несколько тестов ниже на этом
  /// построены. Сам обратный порядок не закрепляется: он существует ради безопасности отписки во время
  /// обхода, а не как обещание вызывающему.</para>
  /// <para>Освобождать подписки в конце теста не нужно: и дерево, и Repo заменяются в SetUp.</para></remarks>
  [TestFixture]
  public class RepoSubscriptionTests : RepoTestBase {

    #region SubscribeAll

    /// <summary>Отказ приходит до того, как реестр подменён: остальные подписчики целы.</summary>
    /// <remarks>Проверка null стоит в выражении присваивания последнего элемента нового массива,
    /// то есть до записи массива в поле. Вторая половина теста и есть смысл первой: без неё
    /// одинаково прошли бы и отказ, и отказ с потерей всех подписчиков.</remarks>
    [Test]
    public void Subscribe_RejectsANullCallback() {
      Action act = () => Topic.Subscribe(null);
      Assert.That(act, Throws.ArgumentNullException);

      Topic t = root.Get("/a");
      t.SetState(Num(1));
      Tick();
      Assert.That(KindsOf(t), Is.EqualTo(new[] { EventKind.Created, EventKind.StateChanged }));
    }

    [Test]
    public void Subscribe_DisposeStopsDelivery() {
      List<TopicEvent> got = new List<TopicEvent>();
      IDisposable sub = Topic.Subscribe(got.Add);
      Topic t = root.Get("/a");
      Tick();
      Assert.That(Kinds(got), Is.EqualTo(new[] { EventKind.Created }));

      sub.Dispose();
      t.SetState(Num(1));
      Tick();
      Assert.That(Kinds(got), Is.EqualTo(new[] { EventKind.Created }));
    }

    /// <summary>Повторный Dispose ничего не отменяет - ни у себя, ни у соседа.</summary>
    /// <remarks>Запись помнит своего владельца и обнуляет его при первом освобождении, а снятие
    /// неизвестного делегата и без того ничего не делает. Без первого второй Dispose снял бы
    /// с учёта другую подписку с тем же делегатом.</remarks>
    [Test]
    public void Subscribe_DisposeTwiceDoesNotUnhookSomebodyElse() {
      List<TopicEvent> x = new List<TopicEvent>();
      List<TopicEvent> y = new List<TopicEvent>();
      IDisposable sx = Topic.Subscribe(x.Add);
      Topic.Subscribe(y.Add);

      sx.Dispose();
      sx.Dispose();

      root.Get("/a");
      Tick();
      Assert.That(x, Is.Empty);
      Assert.That(Kinds(y), Is.EqualTo(new[] { EventKind.Created }));
      Assert.That(Kinds(events), Is.EqualTo(new[] { EventKind.Created }));
    }

    /// <summary>Обработчик может снять чужую подписку прямо во время публикации.</summary>
    /// <remarks>Цикл публикации читает поле подписчиков один раз в локальную переменную и обходит
    /// полученный снимок, поэтому снятие во время обхода не сдвигает индексы под ним. Снятая подписка
    /// остаётся в уже взятом снимке и текущее событие ещё получает; со следующего - нет.</remarks>
    [Test]
    public void Subscribe_ACallbackMayUnsubscribeAnotherOne() {
      Topic t = root.Get("/a");
      Tick();
      ClearEvents();

      List<TopicEvent> victim = new List<TopicEvent>();
      IDisposable sv = Topic.Subscribe(victim.Add);
      Topic.Subscribe(e => sv.Dispose());   // зарегистрирован позже, поэтому вызывается раньше

      t.SetState(Num(1));
      Action act = () => Tick();
      Assert.That(act, Throws.Nothing);
      Assert.That(Kinds(victim), Is.EqualTo(new[] { EventKind.StateChanged }));

      t.SetState(Num(2));
      Tick();
      Assert.That(Kinds(victim), Is.EqualTo(new[] { EventKind.StateChanged }));
    }

    [Test]
    public void Subscribe_ACallbackMayUnsubscribeItself() {
      Topic t = root.Get("/a");
      Tick();
      ClearEvents();

      List<TopicEvent> got = new List<TopicEvent>();
      IDisposable self = null;
      self = Topic.Subscribe(e => { got.Add(e); self.Dispose(); });

      t.SetState(Num(1));
      Action act = () => Tick();
      Assert.That(act, Throws.Nothing);
      t.SetState(Num(2));
      Tick();
      Assert.That(Kinds(got), Is.EqualTo(new[] { EventKind.StateChanged }));
    }

    /// <summary>Подписка, добавленная во время публикации, начинает получать со следующего события.</summary>
    /// <remarks>Снимок массива подписчиков берётся на каждое событие, а не один раз на тик, поэтому
    /// новая подписка вступает в силу внутри того же тика - но не задним числом.</remarks>
    [Test]
    public void Subscribe_ACallbackAddedWhilePublishingStartsAtTheNextEvent() {
      Topic t = root.Get("/a");
      Tick();
      ClearEvents();

      List<TopicEvent> late = new List<TopicEvent>();
      bool added = false;
      Topic.Subscribe(e => {
        if(!added) {
          added = true;
          Topic.Subscribe(late.Add);
        }
      });

      t.SetField("hint", Str("lamp"));   // фаза манифеста - публикуется раньше
      t.SetState(Num(1));                // фаза состояния
      Tick();
      Assert.That(Kinds(late), Is.EqualTo(new[] { EventKind.StateChanged }));
    }

    #endregion SubscribeAll

    #region Snapshot

    /// <summary>Подписка на топике тоже отказывает null-обработчику, и говорит, на каком именно топике.</summary>
    /// <remarks>Однофамилец проверки выше, но другой метод: там реестр всего репозитория, здесь
    /// запись на топике. Сообщение проверяется вместе с отказом, потому что отказ без адреса
    /// одинаково выглядит для любой из подписок сессии - а к моменту, когда он случился, вызвавшего
    /// уже не спросишь.</remarks>
    [Test]
    public void Subscribe_OnATopicRejectsANullCallback() {
      Topic a = root.Get("/a");
      Tick();

      Action act = () => a.Subscribe(SubRec.SubMask.Once | SubRec.SubMask.Value, (Action<TopicEvent, SubRec>)null);
      // Проверяется Message, а не ParamName: на net48 ArgumentNullException складывает сообщение
      // из локализованной части и имени параметра, а локализованная часть нам и не нужна.
      Assert.That(act, Throws.ArgumentNullException.With.Message.Contains("/a").And.Message.Contains("Value"));
    }

    [Test]
    public void Subscribe_OnceSnapshotsTheTopicItself() {
      Topic a = root.Get("/a");
      a.SetState(Num(1));
      Tick();
      ClearEvents();

      List<TopicEvent> got = new List<TopicEvent>();
      Action<TopicEvent, SubRec> h = (e, sr) => got.Add(e);
      SubRec sub = a.Subscribe(SubRec.SubMask.Once | SubRec.SubMask.Value, h);
      Assert.That(got, Is.Empty);   // снимок откладывается до тика, как и всё остальное

      Tick();
      Assert.That(Kinds(got), Is.EqualTo(new[] { EventKind.Snapshot, EventKind.Ready }));
      Assert.That(got[0].Source, Is.SameAs(a));
      Assert.That(got[0].Sub, Is.SameAs(sub));
      Assert.That(got[1].Source, Is.SameAs(a));
    }

    [Test]
    public void Subscribe_ChildrenSnapshotsTheChildrenButNotTheTopic() {
      Topic a = root.Get("/a");
      root.Get("/a/b");
      root.Get("/a/c");
      Tick();
      ClearEvents();

      List<TopicEvent> got = new List<TopicEvent>();
      a.Subscribe(SubRec.SubMask.Children | SubRec.SubMask.Value, (e, sr) => got.Add(e));
      Tick();
      Assert.That(SnapshotPaths(got), Is.EqualTo(new[] { "/a/b", "/a/c" }));
      Assert.That(Kinds(got), Is.EqualTo(new[] { EventKind.Snapshot, EventKind.Snapshot, EventKind.Ready }));
    }

    [Test]
    public void Subscribe_AllSnapshotsTheWholeSubtreeIncludingTheTopic() {
      Topic a = root.Get("/a");
      root.Get("/a/b/d");
      Tick();
      ClearEvents();

      List<TopicEvent> got = new List<TopicEvent>();
      a.Subscribe(SubRec.SubMask.All | SubRec.SubMask.Value, (e, sr) => got.Add(e));
      Tick();
      Assert.That(SnapshotPaths(got), Is.EqualTo(new[] { "/a", "/a/b", "/a/b/d" }));
    }

    /// <summary>All перекрывает Children, а не складывается с ним.</summary>
    [Test]
    public void Subscribe_AllOutranksChildren() {
      Topic a = root.Get("/a");
      root.Get("/a/b");
      Tick();
      ClearEvents();

      List<TopicEvent> got = new List<TopicEvent>();
      a.Subscribe(SubRec.SubMask.Children | SubRec.SubMask.All | SubRec.SubMask.Value, (e, sr) => got.Add(e));
      Tick();
      Assert.That(SnapshotPaths(got), Is.EqualTo(new[] { "/a", "/a/b" }));
    }

    /// <summary>Once и All вместе снимают целевой топик один раз, а не дважды.</summary>
    /// <remarks>Ветка Once отправляет снимок целевого топика безусловно, а глубокий обход начинается
    /// с него же - без явного пропуска подписчик получил бы два Snapshot одного топика. Комбинация
    /// названа прямо в маске: SubMask.OnceOrAll - это ровно Once|All.</remarks>
    [Test]
    public void Subscribe_OnceAndAllSnapshotTheTopicOnlyOnce() {
      Topic a = root.Get("/a");
      root.Get("/a/b");
      Tick();
      ClearEvents();

      List<TopicEvent> got = new List<TopicEvent>();
      a.Subscribe(SubRec.SubMask.OnceOrAll | SubRec.SubMask.Value, (e, sr) => got.Add(e));
      Tick();
      Assert.That(SnapshotPaths(got), Is.EqualTo(new[] { "/a", "/a/b" }));
      Assert.That(Kinds(got), Is.EqualTo(new[] { EventKind.Snapshot, EventKind.Snapshot, EventKind.Ready }));
    }

    /// <summary>Снимок целевого топика приходит даже тогда, когда запрошенного поля у него нет.</summary>
    /// <remarks>Так и задумано: фильтруются события, а не снимок. Подписчик, назвавший префикс,
    /// хочет знать о топике, к которому обратился, независимо от того, заполнен ли уже его манифест;
    /// именно так подписывается MQTT_SN на собственный топик устройства. К поддереву это
    /// не относится - там префикс отбирает узлы.</remarks>
    [Test]
    public void Subscribe_TheOnceSnapshotIgnoresTheFieldFilter() {
      Topic a = root.Get("/a");
      Tick();
      ClearEvents();

      List<TopicEvent> got = new List<TopicEvent>();
      a.Subscribe(SubRec.SubMask.Once | SubRec.SubMask.Field, "MQTT-SN", (e, sr) => got.Add(e));
      Tick();
      Assert.That(a.GetField("MQTT-SN").Defined, Is.False);
      Assert.That(Kinds(got), Is.EqualTo(new[] { EventKind.Snapshot, EventKind.Ready }));
    }

    /// <summary>Подтверждение приходит и тогда, когда снимать было нечего.</summary>
    [Test]
    public void Subscribe_ReadyArrivesEvenWithoutASingleSnapshot() {
      Topic a = root.Get("/a");
      Tick();
      ClearEvents();

      List<TopicEvent> got = new List<TopicEvent>();
      a.Subscribe(SubRec.SubMask.Children | SubRec.SubMask.Value, (e, sr) => got.Add(e));
      Tick();
      Assert.That(Kinds(got), Is.EqualTo(new[] { EventKind.Ready }));
    }

    [Test]
    public void Subscribe_FieldWithAPrefixSkipsTopicsWithoutThatField() {
      Topic a = root.Get("/a");
      Topic x = root.Get("/a/x");
      root.Get("/a/y");
      x.SetField("MQTT.uri", Str("mqtt://localhost"));
      Tick();
      ClearEvents();

      List<TopicEvent> got = new List<TopicEvent>();
      a.Subscribe(SubRec.SubMask.All | SubRec.SubMask.Field, "MQTT", (e, sr) => got.Add(e));
      Tick();
      Assert.That(SnapshotPaths(got), Is.EqualTo(new[] { "/a/x" }));
    }

    /// <summary>Бит Value снимает фильтр по префиксу: подписчик состояния хочет все топики.</summary>
    [Test]
    public void Subscribe_ValueOutranksTheFieldFilter() {
      Topic a = root.Get("/a");
      Topic x = root.Get("/a/x");
      root.Get("/a/y");
      x.SetField("MQTT.uri", Str("mqtt://localhost"));
      Tick();
      ClearEvents();

      List<TopicEvent> got = new List<TopicEvent>();
      a.Subscribe(SubRec.SubMask.All | SubRec.SubMask.Value | SubRec.SubMask.Field, "MQTT", (e, sr) => got.Add(e));
      Tick();
      Assert.That(SnapshotPaths(got), Is.EqualTo(new[] { "/a", "/a/x", "/a/y" }));
    }

    /// <summary>Подписка на манифест без префикса означает любое поле, а не отсутствие полей.</summary>
    /// <remarks>Конструктор SubRec превращает null в пустую строку, когда бит Field установлен,
    /// и фильтр снимка пропускает пустой префикс целиком.</remarks>
    [Test]
    public void Subscribe_FieldWithoutAPrefixSnapshotsEverything() {
      Topic a = root.Get("/a");
      root.Get("/a/x");
      root.Get("/a/y");
      Tick();
      ClearEvents();

      List<TopicEvent> got = new List<TopicEvent>();
      SubRec sub = a.Subscribe(SubRec.SubMask.All | SubRec.SubMask.Field, (e, sr) => got.Add(e));
      Tick();
      Assert.That(sub.prefix, Is.EqualTo(string.Empty));
      Assert.That(SnapshotPaths(got), Is.EqualTo(new[] { "/a", "/a/x", "/a/y" }));
    }

    /// <summary>Повторная подписка тем же обработчиком возвращает прежнюю запись и снимок не повторяет.</summary>
    /// <remarks>Один делегат хранится в переменной намеренно: две одинаковые с виду лямбды - это
    /// два разных экземпляра, и поиск существующей записи их не сопоставит.</remarks>
    [Test]
    public void Subscribe_TheSameHandlerTwiceIsNotSnapshottedAgain() {
      Topic a = root.Get("/a");
      Tick();
      ClearEvents();

      List<TopicEvent> got = new List<TopicEvent>();
      Action<TopicEvent, SubRec> h = (e, sr) => got.Add(e);
      SubRec first = a.Subscribe(SubRec.SubMask.Once | SubRec.SubMask.Value, h);
      Tick();
      Assert.That(Kinds(got), Is.EqualTo(new[] { EventKind.Snapshot, EventKind.Ready }));

      got.Clear();
      SubRec second = a.Subscribe(SubRec.SubMask.Once | SubRec.SubMask.Value, h);
      Assert.That(second, Is.SameAs(first));
      Tick();
      Assert.That(Kinds(got), Is.EqualTo(new[] { EventKind.Ready }));
    }

    /// <summary>Повторная подписка с битом Field и без префикса тоже должна найти прежнюю запись.</summary>
    /// <remarks>Проверка выше проходит мимо этого случая: она подписывается с Once|Value, то есть по
    /// ветке (mask &amp; Field) == None, где префикс не сравнивается вовсе. Здесь бит Field установлен,
    /// и сравнение выполняется - причём сравнивается нормализованный префикс уже созданной записи
    /// (конструктор SubRec заменил null на пустую строку) с сырым аргументом, который так и остался null.</remarks>
    [Test]
    public void Subscribe_TheSameHandlerWithTheFieldBitIsNotDuplicated() {
      Topic a = root.Get("/a");
      Tick();
      ClearEvents();

      List<TopicEvent> got = new List<TopicEvent>();
      Action<TopicEvent, SubRec> h = (e, sr) => got.Add(e);
      SubRec first = a.Subscribe(SubRec.SubMask.Once | SubRec.SubMask.Field, h);
      Tick();
      Assert.That(Kinds(got), Is.EqualTo(new[] { EventKind.Snapshot, EventKind.Ready }));

      got.Clear();
      SubRec second = a.Subscribe(SubRec.SubMask.Once | SubRec.SubMask.Field, h);
      Assert.That(second, Is.SameAs(first));
      Tick();
      Assert.That(Kinds(got), Is.EqualTo(new[] { EventKind.Ready }));
    }

    /// <summary>Цена дубликата: одно изменение, доставленное обработчику дважды.</summary>
    /// <remarks>Предмет проверки - по-прежнему устранение дубликатов в Subscribe, а не фильтр доставки,
    /// поэтому тест стоит здесь, а не в RepoDeliveryTests; доставка тут лишь способ увидеть вторую запись.
    /// Снимок отбрасывается до записи, чтобы в списке остались только изменения.</remarks>
    [Test]
    public void Subscribe_TheSameHandlerWithTheFieldBitIsDeliveredOnce() {
      Topic a = root.Get("/a");
      Tick();

      List<TopicEvent> got = new List<TopicEvent>();
      Action<TopicEvent, SubRec> h = (e, sr) => got.Add(e);
      a.Subscribe(SubRec.SubMask.Once | SubRec.SubMask.Field, h);
      a.Subscribe(SubRec.SubMask.Once | SubRec.SubMask.Field, h);
      Tick();
      got.Clear();

      a.SetField("hint", Str("lamp"));
      Tick();
      Assert.That(Kinds(got), Is.EqualTo(new[] { EventKind.FieldChanged }));
    }

    /// <summary>Снимок предшествует изменениям того же тика, но значения не несёт.</summary>
    /// <remarks>Фаза подписки идёт раньше фазы состояния, поэтому Snapshot публикуется первым.
    /// Но к моменту публикации применён уже весь пакет, и топик содержит новое значение - новый
    /// подписчик законно видит одно и то же значение дважды: в снимке и в изменении.</remarks>
    [Test]
    public void Subscribe_TheSnapshotPrecedesChangesMadeInTheSameTick() {
      Topic a = root.Get("/a");
      a.SetState(Num(1));
      Tick();
      ClearEvents();

      List<TopicEvent> got = new List<TopicEvent>();
      List<int> seen = new List<int>();
      a.Subscribe(SubRec.SubMask.All | SubRec.SubMask.Value, (e, sr) => { got.Add(e); seen.Add((int)e.Source.GetState()); });
      a.SetState(Num(9));
      Tick();
      Assert.That(Kinds(got), Is.EqualTo(new[] { EventKind.Snapshot, EventKind.StateChanged, EventKind.Ready }));
      Assert.That(seen[0], Is.EqualTo(9));
    }

    /// <summary>Топик, созданный в том же тике, объявляется раньше, чем попадает в снимок.</summary>
    /// <remarks>Порядок обратен ожидаемому: фаза структуры идёт раньше фазы подписки. А в снимок
    /// новый топик попадает потому, что структура меняется сразу, на потоке вызвавшего, и к моменту
    /// разбора очереди узел уже стоит в дереве.</remarks>
    [Test]
    public void Subscribe_ATopicCreatedInTheSameTickIsAnnouncedBeforeItsSnapshot() {
      Topic a = root.Get("/a");
      Tick();
      ClearEvents();

      List<TopicEvent> got = new List<TopicEvent>();
      a.Subscribe(SubRec.SubMask.All | SubRec.SubMask.Value, (e, sr) => got.Add(e));
      Topic n = root.Get("/a/n");
      Tick();
      Assert.That(Kinds(got), Is.EqualTo(new[] { EventKind.Created, EventKind.Snapshot, EventKind.Snapshot, EventKind.Ready }));
      Assert.That(got[0].Source, Is.SameAs(n));
      Assert.That(SnapshotPaths(got), Is.EqualTo(new[] { "/a", "/a/n" }));
    }

    #endregion Snapshot

    /// <summary>Пути топиков, попавших в снимок, в порядке публикации.</summary>
    private static string[] SnapshotPaths(IEnumerable<TopicEvent> src) {
      return src.Where(e => e.Kind == EventKind.Snapshot).Select(e => e.Source.path).ToArray();
    }
  }
}
