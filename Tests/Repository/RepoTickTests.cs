///<remarks>Этот файл является частью проекта <see cref="https://github.com/enviriot">Enviriot</see>.<remarks>
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using X13.Repository;

namespace X13.Tests {
  /// <summary>Тик: очередь, порядок фаз, объединение повторных записей и изоляция сбоев.</summary>
  /// <remarks>Предмет проверки здесь - не то, что изменение доходит до дерева, а то, в каком порядке
  /// и вместе с чем оно доходит. Весь пакет применяется до начала публикации, поэтому подписчик
  /// видит согласованное дерево; повторная запись в один топик за тик даёт одно событие; ни сбойная
  /// команда, ни сбойный подписчик не уносят с собой остаток пакета.
  /// <para>Инструмент - унаследованный от базы коллектор events: он получает всё, что репозиторий
  /// опубликовал, без масок и фильтров. Маски подписок проверяются в RepoSubscriptionTests.</para></remarks>
  [TestFixture]
  public class RepoTickTests : RepoTestBase {

    #region Queue

    /// <summary>Пустая запись в очереди отбрасывается и остаток пакета не трогает.</summary>
    /// <remarks>Ставится первой намеренно: если бы она приводила к исключению, потерялась бы
    /// следующая команда, и проверка состояния это увидела бы. Проверка же самой пустой команды
    /// ничего не доказывает - она и так ничего не делает.</remarks>
    [Test]
    public void DoCmd_NullCommandIsSkipped() {
      repo.DoCmd(null);
      Topic t = root.Get("/a");
      t.SetState(Num(1));
      Tick();
      Assert.That((int)t.GetState(), Is.EqualTo(1));
      Assert.That(KindsOf(t), Is.EqualTo(new[] { EventKind.Created, EventKind.StateChanged }));
    }

    [Test]
    public void DoCmd_CommandWithoutATargetIsSkipped() {
      repo.DoCmd(new CmdState(null, Num(1), null));
      Topic t = root.Get("/a");
      t.SetState(Num(1));
      Tick();
      Assert.That((int)t.GetState(), Is.EqualTo(1));
      Assert.That(KindsOf(t), Is.EqualTo(new[] { EventKind.Created, EventKind.StateChanged }));
    }

    /// <summary>Команда, поставленная подписчиком, относится к следующему тику.</summary>
    /// <remarks>Очередь разбирается целиком до применения, а публикация идёт последней, поэтому
    /// к моменту вызова подписчика разбирать уже нечего. Иначе тик был бы способен не завершиться:
    /// два подписчика, отвечающих друг другу, продлевали бы его бесконечно.</remarks>
    [Test]
    public void DoCmd_QueuedWhilePublishingWaitsForTheNextTick() {
      Topic a = root.Get("/a");
      Topic b = root.Get("/b");
      Tick();
      ClearEvents();

      Topic.Subscribe(e => {
        if(e.Source == a && e.Kind == EventKind.StateChanged) {
          b.SetState(Num(2));
        }
      });
      a.SetState(Num(1));
      Tick();
      Assert.That(b.GetState().Defined, Is.False);

      Tick();
      Assert.That((int)b.GetState(), Is.EqualTo(2));
    }

    #endregion Queue

    #region Phases

    /// <summary>Весь пакет применён до того, как опубликовано первое событие.</summary>
    /// <remarks>Это главное обещание тика. Манифест - фаза раньше состояния, поэтому FieldChanged
    /// публикуется раньше StateChanged; но состояние к этому моменту уже записано, потому что
    /// применение всех фаз завершилось до начала публикации. Подписчик, читающий соседний топик,
    /// видит картину целиком, а не промежуточную.</remarks>
    [Test]
    public void Tick_AppliesEverythingBeforePublishingAnything() {
      Topic a = root.Get("/a");
      Topic b = root.Get("/b");
      Tick();
      ClearEvents();

      int seen = -1;
      Topic.Subscribe(e => {
        if(e.Kind == EventKind.FieldChanged) {
          seen = (int)b.GetState();
        }
      });
      a.SetField("hint", Str("lamp"));
      b.SetState(Num(7));
      Tick();
      Assert.That(Kinds(events), Is.EqualTo(new[] { EventKind.FieldChanged, EventKind.StateChanged }));
      Assert.That(seen, Is.EqualTo(7));
    }

    /// <summary>Порядок фаз: структура, манифест, состояние, удаление.</summary>
    [Test]
    public void Tick_PublishesStructureBeforeContentBeforeRemoval() {
      Topic old = root.Get("/old");
      Tick();
      ClearEvents();

      Topic n = root.Get("/n");
      n.SetField("hint", Str("lamp"));
      n.SetState(Num(1));
      old.Remove();
      Tick();
      Assert.That(Kinds(events), Is.EqualTo(new[] {
        EventKind.Created, EventKind.FieldChanged, EventKind.StateChanged, EventKind.Removed }));
    }

    /// <summary>Подтверждение подписки идёт последним - после изменений, к ней не относящихся.</summary>
    [Test]
    public void Tick_AcknowledgementIsTheLastEvent() {
      Topic a = root.Get("/a");
      Topic b = root.Get("/b");
      Tick();
      ClearEvents();

      a.Subscribe(SubRec.SubMask.Once | SubRec.SubMask.Value, (e, sr) => { });
      b.SetState(Num(1));
      Tick();
      Assert.That(Kinds(events), Is.EqualTo(new[] { EventKind.Snapshot, EventKind.StateChanged, EventKind.Ready }));
    }

    /// <summary>Запись в топик, удаляемый тем же тиком, всё равно происходит и объявляется.</summary>
    /// <remarks>Фаза удаления идёт после фазы состояния именно поэтому: изменение топика, удаляемого
    /// в том же тике, относится ещё к тому тику, в котором он существовал. Подписчик получает
    /// значение, а следом - сообщение о том, что топика больше нет, и в нём же прежнее значение.</remarks>
    [Test]
    public void Tick_AChangeToARemovedTopicStillHappensFirst() {
      Topic t = root.Get("/a");
      Tick();
      ClearEvents();

      t.SetState(Num(5));
      t.Remove();
      Tick();
      Assert.That(KindsOf(t), Is.EqualTo(new[] { EventKind.StateChanged, EventKind.Removed }));
      Assert.That((int)EventOf(t, EventKind.Removed).OldState, Is.EqualTo(5));
      Assert.That(t.GetState().IsNull, Is.True);
    }

    #endregion Phases

    #region Coalescing

    /// <summary>Повторная запись в поле занимает место первой, а не встаёт в конец.</summary>
    /// <remarks>Порядок событий пакета - это порядок первых изменений, а не последних. Иначе
    /// топик, чьё поле переписали дважды, каждый раз менял бы место в потоке событий в зависимости
    /// от того, сколько раз в него успели написать.</remarks>
    [Test]
    public void SetField_TheSecondWriteKeepsThePlaceOfTheFirst() {
      Topic t = root.Get("/a");
      Tick();
      ClearEvents();

      t.SetField("hint", Str("first"));
      t.SetField("type", Str("Bool"));
      t.SetField("hint", Str("second"));
      Tick();
      Assert.That(events.Select(e => e.FieldPath).ToArray(), Is.EqualTo(new[] { "hint", "type" }));
      Assert.That(t.GetField("hint").Value, Is.EqualTo("second"));
    }

    [Test]
    public void SetState_TheSecondWriteKeepsThePlaceOfTheFirst() {
      Topic a = root.Get("/a");
      Topic b = root.Get("/b");
      Tick();
      ClearEvents();

      a.SetState(Num(1));
      b.SetState(Num(2));
      a.SetState(Num(3));
      Tick();
      Assert.That(Paths(events.Select(e => e.Source)), Is.EqualTo(new[] { "/a", "/b" }));
      Assert.That((int)a.GetState(), Is.EqualTo(3));
    }

    /// <summary>Одинаковое имя поля у двух топиков - это две разные записи, а не одна.</summary>
    /// <remarks>Ключ объединения содержит и топик, и путь поля, причём топик сравнивается по ссылке.
    /// Если бы ключом был только путь, вторая запись вытеснила бы первую и одно из значений
    /// исчезло бы вместе со своим событием.</remarks>
    [Test]
    public void SetField_TheSameFieldOfTwoTopicsIsNotOneSlot() {
      Topic a = root.Get("/a");
      Topic b = root.Get("/b");
      Tick();
      ClearEvents();

      a.SetField("hint", Str("one"));
      b.SetField("hint", Str("two"));
      Tick();
      Assert.That(Kinds(events), Is.EqualTo(new[] { EventKind.FieldChanged, EventKind.FieldChanged }));
      Assert.That(a.GetField("hint").Value, Is.EqualTo("one"));
      Assert.That(b.GetField("hint").Value, Is.EqualTo("two"));
    }

    /// <summary>Удаление потомка и следом родителя объявляет каждого один раз.</summary>
    /// <remarks>Каждое удаление разворачивается на всё поддерево, поэтому без защиты потомок попал бы
    /// в пакет дважды. Защита - флаг disposed: Remove() ставит его сразу, на потоке вызвавшего,
    /// и обход поддерева родителя уже помеченного потомка не видит.
    /// <para>Проверяется число событий, а не дерево: повторное отсоединение сравнивает пару "имя -
    /// топик" и молча ничего не делает, поэтому в дереве двойное удаление неразличимо.</para></remarks>
    [Test]
    public void Remove_OfAChildAndThenItsParentAnnouncesEachOnce() {
      Topic a = root.Get("/a");
      Topic b = root.Get("/a/b");
      Tick();
      ClearEvents();

      b.Remove();
      a.Remove();
      Tick();
      Assert.That(KindsOf(b), Is.EqualTo(new[] { EventKind.Removed }));
      Assert.That(KindsOf(a), Is.EqualTo(new[] { EventKind.Removed }));
      Assert.That(events.Count, Is.EqualTo(2));
    }

    #endregion Coalescing

    #region Isolation

    /// <summary>Сбой при применении одной команды не уносит остаток пакета.</summary>
    /// <remarks>Сбойная команда стоит в фазе структуры, то есть раньше фазы состояния: проверка
    /// показывает, что после исключения тик дошёл до следующих фаз, а не только до следующей
    /// команды той же фазы.</remarks>
    [Test]
    public void Tick_AFailingCommandDoesNotLoseTheRest() {
      Topic t = root.Get("/a");
      Tick();
      ClearEvents();

      repo.DoCmd(new FailingCmd(t));
      t.SetState(Num(1));
      Action act = () => Tick();
      Assert.That(act, Throws.Nothing);
      Assert.That((int)t.GetState(), Is.EqualTo(1));
      Assert.That(KindsOf(t), Is.EqualTo(new[] { EventKind.StateChanged }));
    }

    /// <summary>Команда, которую не удалось разложить по фазам, теряется одна.</summary>
    [Test]
    public void Tick_ACommandThatCannotBeDispatchedDoesNotLoseTheRest() {
      Topic t = root.Get("/a");
      Tick();
      ClearEvents();

      repo.DoCmd(new BadPhaseCmd(t));
      t.SetState(Num(1));
      Action act = () => Tick();
      Assert.That(act, Throws.Nothing);
      Assert.That((int)t.GetState(), Is.EqualTo(1));
      Assert.That(KindsOf(t), Is.EqualTo(new[] { EventKind.StateChanged }));
    }

    /// <summary>Сбойный подписчик лишает события только себя.</summary>
    /// <remarks>Коллектор зарегистрирован раньше сбойного, а обход идёт с конца массива - значит,
    /// вызывается после него. Без этого порядка тест прошёл бы и в том случае, если исключение
    /// прерывает обход: до коллектора очередь просто не дошла бы.</remarks>
    [Test]
    public void Tick_AFailingSubscriberDoesNotStopTheOthers() {
      Topic t = root.Get("/a");
      Tick();
      ClearEvents();

      List<TopicEvent> got = new List<TopicEvent>();
      Topic.Subscribe(got.Add);
      Topic.Subscribe(e => { throw new InvalidOperationException("boom"); });

      t.SetState(Num(1));
      Action act = () => Tick();
      Assert.That(act, Throws.Nothing);
      Assert.That(Kinds(got), Is.EqualTo(new[] { EventKind.StateChanged }));
      Assert.That(Kinds(events), Is.EqualTo(new[] { EventKind.StateChanged }));
    }

    /// <summary>Перехват стоит на паре "событие - подписчик", а не на тике.</summary>
    /// <remarks>Иначе один сбой в начале тика отключал бы подписчика до конца пакета, и он пропускал
    /// бы изменения, к которым его неисправность отношения не имеет.</remarks>
    [Test]
    public void Tick_AFailingSubscriberStillGetsTheNextEvent() {
      Topic t = root.Get("/a");
      Tick();
      ClearEvents();

      List<TopicEvent> got = new List<TopicEvent>();
      Topic.Subscribe(e => { got.Add(e); throw new InvalidOperationException("boom"); });

      t.SetField("hint", Str("lamp"));
      t.SetState(Num(1));
      Tick();
      Assert.That(Kinds(got), Is.EqualTo(new[] { EventKind.FieldChanged, EventKind.StateChanged }));
    }

    /// <summary>Частично обработанный пакет не публикуется повторно следующим тиком.</summary>
    /// <remarks>Списки фаз и событий очищаются в finally, то есть и после исключения тоже.</remarks>
    [Test]
    public void Tick_DoesNotRepublishAPartlyProcessedBatch() {
      Topic t = root.Get("/a");
      Tick();
      ClearEvents();

      repo.DoCmd(new FailingCmd(t));
      t.SetState(Num(1));
      Tick();
      ClearEvents();

      Tick();
      Assert.That(events, Is.Empty);
    }

    #endregion Isolation

    #region Reentrancy

    /// <summary>Тик, вызванный из подписчика, не делает ничего - и ничего не портит.</summary>
    /// <remarks>Флаг занятости проверяется до входа в тело, поэтому вложенный вызов возвращается
    /// раньше, чем дошёл бы до очистки списков в finally. Если бы он до неё дошёл, внешний тик
    /// потерял бы остаток собственного пакета - поэтому проверяется в первую очередь, что второе
    /// событие того же тика доставлено.</remarks>
    [Test]
    public void Tick_FromInsideASubscriberDoesNothing() {
      Topic a = root.Get("/a");
      Topic b = root.Get("/b");
      Tick();
      ClearEvents();

      List<TopicEvent> got = new List<TopicEvent>();
      bool nested = false;
      Topic.Subscribe(e => {
        got.Add(e);
        if(!nested) {
          nested = true;
          b.SetState(Num(2));
          Tick();
        }
      });
      a.SetField("hint", Str("lamp"));
      a.SetState(Num(1));
      Tick();
      Assert.That(Kinds(got), Is.EqualTo(new[] { EventKind.FieldChanged, EventKind.StateChanged }));
      Assert.That(b.GetState().Defined, Is.False);

      Tick();
      Assert.That((int)b.GetState(), Is.EqualTo(2));
    }

    /// <summary>До Init тик молчит: флаг занятости поднимает именно Init.</summary>
    /// <remarks>Этот репозиторий намеренно не инициализируется - Init() заменил бы дерево под базой
    /// вместе с её подпиской, и дальнейшие проверки прошли бы просто потому, что слушать стало
    /// некому.</remarks>
    [Test]
    public void Tick_BeforeInitDoesNothing() {
      Topic t = root.Get("/a");
      Tick();
      ClearEvents();

      Repo fresh = new Repo();
      fresh.DoCmd(new CmdState(t, Num(1), null));
      Action act = () => fresh.Tick();
      Assert.That(act, Throws.Nothing);
      Assert.That(t.GetState().Defined, Is.False);
      Assert.That(events, Is.Empty);
    }

    #endregion Reentrancy

    #region SaveConfig

    /// <summary>Изменение Config-топика взводит будильник, а не пишет файл.</summary>
    /// <remarks>Сохранение отложено на пять секунд, поэтому проверяется только отрицательная
    /// половина: сразу после изменения файла нет. Положительная стоила бы пяти секунд сна и
    /// настоящей записи на диск.</remarks>
    [Test]
    public void Start_DoesNotSaveBeforeTheDelay() {
      Repo.configPath = _cfgPath;
      repo.Start();

      Topic t = root.Get("/a");
      t.SetAttribute(Topic.Attribute.Config);
      Tick();
      t.SetState(Num(1));
      Tick();
      Assert.That(t.CheckAttribute(Topic.Attribute.Saved, Topic.Attribute.Config), Is.True);
      Assert.That(File.Exists(_cfgPath), Is.False);
    }

    /// <summary>Путь задаётся статически и на один тест, поэтому убирается здесь же.</summary>
    [TearDown]
    public void RepoTickTestsTearDown() {
      Repo.configPath = null;
      try {
        File.Delete(_cfgPath);   // не выбрасывает исключение, если файла нет
      }
      catch(IOException) {
      }
    }
    private static readonly string _cfgPath = Path.Combine(Path.GetTempPath(), "X13.Tests.SaveConfig.xst");

    #endregion SaveConfig

    /// <summary>Команда, которая падает при применении. Тик обязан потерять только её.</summary>
    private sealed class FailingCmd : Cmd {
      public FailingCmd(Topic target) : base(target, null) { }
      public override Phase Phase { get { return Phase.Struct; } }
      public override TopicEvent Apply() { throw new InvalidOperationException("boom"); }
    }

    /// <summary>Команда, которую невозможно разложить по фазам: такой фазы нет.</summary>
    private sealed class BadPhaseCmd : Cmd {
      public BadPhaseCmd(Topic target) : base(target, null) { }
      public override Phase Phase { get { return (Phase)99; } }
      public override TopicEvent Apply() { return null; }
    }
  }
}
