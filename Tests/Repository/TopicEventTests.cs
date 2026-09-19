///<remarks>Этот файл является частью проекта <see cref="https://github.com/enviriot">Enviriot</see>.<remarks>
using System.Linq;
using NUnit.Framework;
using X13.Repository;

namespace X13.Tests {
  /// <summary>Автор изменения: откуда TopicEvent.Author берётся и до кого доходит.</summary>
  /// <remarks>Предмет проверки здесь - не само изменение, а подпись под ним. На ней держится защита
  /// от эха: плагин отбрасывает событие с Author == Owner, считая его собственной записью, вернувшейся
  /// обратно. Поэтому потерянный по дороге автор не портит дерево и ни один тест дерева его не хватится -
  /// он замыкает петлю в работающей системе.
  /// <para>Автор - обычный топик, а не идентификатор: сравнение всегда по ссылке, поэтому и здесь
  /// используется Is.SameAs.</para></remarks>
  [TestFixture]
  public class TopicEventTests : RepoTestBase {

    /// <summary>Топик, от имени которого выполняются записи.</summary>
    /// <remarks>Создаётся до ClearEvents: его собственное появление - такое же событие, и в проверяемом
    /// наборе ему делать нечего. Метод, а не поле: NUnit переиспользует экземпляр обвязки, и поле,
    /// оставшееся от предыдущего теста, указывало бы на топик уже заменённого дерева.</remarks>
    private Topic Author() {
      Topic a = root.Get("/dev/mqtt");
      Tick();
      ClearEvents();
      return a;
    }

    /// <summary>Не указанный автор остаётся не указанным, а не подменяется топиком.</summary>
    [Test]
    public void Author_IsNullWhenNobodyIsNamed() {
      Topic t = root.Get("/a");
      t.SetState(Num(1));
      t.SetField("hint", Str("lamp"));
      Tick();
      Assert.That(KindsOf(t), Is.EqualTo(new[] { EventKind.Created, EventKind.FieldChanged, EventKind.StateChanged }));
      Assert.That(events.Count(e => e.Source == t && e.Author != null), Is.EqualTo(0));
    }

    [Test]
    public void Author_OfAStateChangeIsTheOneWhoWrote() {
      Topic a = Author();
      Topic t = root.Get("/a");
      t.SetState(Num(1), a);
      Tick();
      Assert.That(EventOf(t, EventKind.StateChanged).Author, Is.SameAs(a));
    }

    [Test]
    public void Author_OfAFieldChangeIsTheOneWhoWrote() {
      Topic a = Author();
      Topic t = root.Get("/a");
      t.SetField("MQTT.uri", Str("mqtt://localhost"), a);
      Tick();
      Assert.That(EventOf(t, EventKind.FieldChanged).Author, Is.SameAs(a));
    }

    /// <summary>Создание подписывает каждый топик пути, а не только последний.</summary>
    /// <remarks>Промежуточные узлы создаются тем же вызовом и появляются для подписчика так же внезапно,
    /// как и запрошенный: если бы они приходили неподписанными, плагин принял бы их за чужую работу.</remarks>
    [Test]
    public void Author_OfACreationIsTheOneWhoAskedForThePath() {
      Topic a = Author();
      root.Get("/x/y/z", true, a);
      Tick();
      Assert.That(events.Select(e => e.Source.path).ToArray(), Is.EqualTo(new[] { "/x", "/x/y", "/x/y/z" }));
      Assert.That(events.Count(e => e.Author != a), Is.EqualTo(0));
    }

    [Test]
    public void Author_OfAMoveIsTheOneWhoMoved() {
      Topic a = Author();
      Topic t = root.Get("/a");
      Topic p = root.Get("/b");
      Tick();
      ClearEvents();
      t.Move(p, "c", a);
      Tick();
      Assert.That(EventOf(t, EventKind.Moved).Author, Is.SameAs(a));
    }

    /// <summary>Удаление подписывает всё поддерево, а не только тот топик, у которого вызвали Remove.</summary>
    /// <remarks>Каждому топику поддерева создаётся собственная команда удаления, и автор в неё переносится
    /// с команды, поставленной вызывающим кодом. Потомок ни о каком авторе не знает - подпись у него
    /// может быть только унаследованной.</remarks>
    [Test]
    public void Author_OfARemovalReachesTheWholeSubtree() {
      Topic a = Author();
      Topic p = root.Get("/p");
      Topic c = root.Get("/p/c");
      Tick();
      ClearEvents();
      p.Remove(a);
      Tick();
      Assert.That(EventOf(p, EventKind.Removed).Author, Is.SameAs(a));
      Assert.That(EventOf(c, EventKind.Removed).Author, Is.SameAs(a));
    }

    /// <summary>У снимка и подтверждения автор - сам топик, а не тот, кто подписался.</summary>
    /// <remarks>Следствие важнее самого факта: плагин, владеющий топиком и на него же подписанный,
    /// отбросил бы собственный снимок обычной проверкой Author == Owner и остался бы без начального
    /// состояния. Поэтому такая проверка в MsDevice сделана с исключением по виду события, а LiteDB_Pl
    /// отбрасывает Snapshot и Ready ещё до того, как смотрит на автора.</remarks>
    [Test]
    public void Author_OfASnapshotIsTheTopicItself() {
      Topic a = root.Get("/a");
      Topic x = root.Get("/a/x");
      Tick();
      ClearEvents();
      a.Subscribe(SubRec.SubMask.All | SubRec.SubMask.Value, (e, sr) => { });
      Tick();
      Assert.That(EventOf(a, EventKind.Snapshot).Author, Is.SameAs(a));
      Assert.That(EventOf(x, EventKind.Snapshot).Author, Is.SameAs(x));
      Assert.That(EventOf(a, EventKind.Ready).Author, Is.SameAs(a));
    }
  }
}
