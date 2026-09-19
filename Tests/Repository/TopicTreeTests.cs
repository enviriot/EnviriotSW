///<remarks>Этот файл является частью проекта <see cref="https://github.com/enviriot">Enviriot</see>.<remarks>
using System;
using NUnit.Framework;
using X13.Repository;

namespace X13.Tests {
  /// <summary>Структура дерева: поиск по пути, создание, перемещение, удаление, обход.</summary>
  /// <remarks>Структура меняется сразу и на потоке вызвавшего, а события откладываются до тика -
  /// это разделение проверяется почти в каждом разделе, потому что именно из-за него "изменение
  /// применено" и "изменение опубликовано" не одно и то же.</remarks>
  [TestFixture]
  public class TopicTreeTests : RepoTestBase {

    /// <summary>Каждый тест начинается с пустого корня - на этом держится вся изоляция.</summary>
    [Test]
    public void Root_IsEmptyAndCarriesItsOwnAttributes() {
      Assert.That(root.path, Is.EqualTo("/"));
      Assert.That(root.parent, Is.Null);
      Assert.That(root.HasChildren(), Is.False);
      Assert.That(root.CheckAttribute(Topic.Attribute.Required), Is.True);
      Assert.That(root.CheckAttribute(Topic.Attribute.Internal), Is.True);
    }

    #region Resolve

    [Test]
    public void Get_RootPathAlwaysReturnsRoot() {
      Topic deep = root.Get("/dev/light1");
      Assert.That(deep.Get("/"), Is.SameAs(root));
    }

    [Test]
    public void Get_EmptyPathReturnsHome() {
      Topic t = root.Get("/dev");
      Assert.That(t.Get(null), Is.SameAs(t));
      Assert.That(t.Get(string.Empty), Is.SameAs(t));
    }

    [Test]
    public void Get_CreatesTheWholeChain() {
      Topic t = root.Get("/dev/light1/state");
      Assert.That(t.path, Is.EqualTo("/dev/light1/state"));
      Assert.That(t.name, Is.EqualTo("state"));
      Assert.That(t.parent.path, Is.EqualTo("/dev/light1"));
      Assert.That(t.parent.parent.path, Is.EqualTo("/dev"));
      Assert.That(t.parent.parent.parent, Is.SameAs(root));
    }

    /// <summary>Ребёнок корня - "/dev", а не "//dev": путь корня уже заканчивается разделителем.</summary>
    [Test]
    public void Get_ChildOfRootHasNoDoubleSeparator() {
      Assert.That(root.Get("dev").path, Is.EqualTo("/dev"));
    }

    [Test]
    public void Get_SamePathReturnsTheSameInstance() {
      Topic first = root.Get("/a/b");
      Assert.That(root.Get("/a/b"), Is.SameAs(first));
    }

    [Test]
    public void Get_RelativePathIsResolvedUnderHome() {
      Topic home = root.Get("/dev");
      Assert.That(home.Get("light1/state").path, Is.EqualTo("/dev/light1/state"));
    }

    [Test]
    public void Get_AbsolutePathUnderHomeIsRebased() {
      Topic home = root.Get("/dev/light1");
      Assert.That(home.Get("/dev/light1/state"), Is.SameAs(home.Get("state")));
    }

    [Test]
    public void Get_AbsolutePathOutsideHomeRestartsFromRoot() {
      Topic home = root.Get("/a/b");
      Assert.That(home.Get("/c/d").path, Is.EqualTo("/c/d"));
    }

    /// <summary>Совпадение префикса засчитывается только на границе сегмента.</summary>
    /// <remarks>Иначе "/dev/light10/state" при home "/dev/light1" превратился бы в "0/state" под
    /// ним: топик с именем "0", до которого никто никогда не обратится.</remarks>
    [Test]
    public void Get_PrefixMustEndOnASegmentBoundary() {
      Topic home = root.Get("/dev/light1");
      Topic t = home.Get("/dev/light10/state");
      Assert.That(t.path, Is.EqualTo("/dev/light10/state"));
      Assert.That(home.Exist("0"), Is.False);
    }

    [Test]
    public void Get_WithoutCreateAnswersNullAndCreatesNothing() {
      Assert.That(root.Get("/nope/deep", false), Is.Null);
      Assert.That(root.HasChildren(), Is.False);
    }

    [Test]
    public void Exist_SetsTheOutParameterOnlyWhenFound() {
      Topic t = root.Get("/dev");
      Topic found;
      Assert.That(root.Exist("/dev", out found), Is.True);
      Assert.That(found, Is.SameAs(t));
      Assert.That(root.Exist("/nope", out found), Is.False);
      Assert.That(found, Is.Null);
    }

    /// <summary>Команда отказывает с ArgumentException, называя то, что ей не подошло.</summary>
    [TestCase("/dev/#/x")]
    [TestCase("/dev/+")]
    [TestCase("/dev/ /x")]
    public void Get_BadSegmentThrows(string path) {
      Action act = () => root.Get(path);
      Assert.That(act, Throws.ArgumentException);
    }

    /// <summary>Имя проверяется у тех сегментов, до которых обход дошёл, и только у них.</summary>
    /// <remarks>Вопрос об отсутствующем пути отвечает null на первом же недостающем сегменте и
    /// дальше не смотрит, поэтому непригодное имя за ним остаётся непрочитанным. Но если обход
    /// до него дошёл, ответом будет исключение, а не null: правило "вопрос отвечает, а не
    /// бросает" относится к ненайденному топику, а не к пути, которого в дереве быть не может.</remarks>
    [Test]
    public void Get_WithoutCreate_ChecksOnlyTheSegmentsItReaches() {
      Assert.That(root.Get("/nope/#/x", false), Is.Null);   // до "#" обход не дошёл
      root.Get("/dev");
      Action act = () => root.Get("/dev/#/x", false);
      Assert.That(act, Throws.ArgumentException);
    }

    [Test]
    public void Get_CreationIsAnnouncedOnTheNextTick() {
      Topic t = root.Get("/dev");
      Assert.That(events, Is.Empty);
      Tick();
      Assert.That(KindsOf(t), Is.EqualTo(new[] { EventKind.Created }));
    }

    #endregion Resolve

    #region Declare + Fill

    /// <summary>Declare создаёт топик и не объявляет о нём: это делает Fill.</summary>
    /// <remarks>Значит, объявленный и незаполненный топик невидим: он в дереве, находится по
    /// пути, и ни одно событие не сообщило, что он появился.</remarks>
    [Test]
    public void Declare_CreatesTheTopicButAnnouncesNothing() {
      Topic t = Topic.Declare(root, "/dev/x");
      Assert.That(root.Get("/dev/x", false), Is.SameAs(t));
      Tick();
      Assert.That(KindsOf(t), Is.Empty);
      Assert.That(events, Is.Empty);
    }

    [Test]
    public void Declare_LeavesNoManifestUntilFill() {
      Topic t = Topic.Declare(root, "/dev/x");
      Assert.That(t.GetField(null).Defined, Is.False);
      Assert.That(t.GetField("attr").Defined, Is.False);
      Assert.That(t.CheckAttribute(Topic.Attribute.Required), Is.False);
    }

    [Test]
    public void Fill_SetsTheStateAtOnceAndAnnouncesTheTopic() {
      Topic t = Topic.Declare(root, "/dev/x");
      Topic.Fill(t, Num(42), JsLib.ParseJson("{\"attr\":8,\"hint\":\"lamp\"}"), null);
      Assert.That((int)t.GetState(), Is.EqualTo(42));   // состояние ставится напрямую, минуя тик
      Assert.That((int)t.GetField("attr"), Is.EqualTo(8));
      Assert.That(t.GetField("hint").Value, Is.EqualTo("lamp"));
      Assert.That(events, Is.Empty);
      Tick();
      Assert.That(KindsOf(t), Is.EqualTo(new[] { EventKind.Created }));
    }

    [Test]
    public void Fill_ForcesANumericAttrIntoAManifestWithoutOne() {
      Topic t = Topic.Declare(root, "/dev/x");
      Topic.Fill(t, null, JsLib.ParseJson("{\"hint\":\"lamp\"}"), null);
      Assert.That(t.GetField("attr").IsNumber, Is.True);
      Assert.That((int)t.GetField("attr"), Is.EqualTo(0));
      Assert.That(t.GetField("hint").Value, Is.EqualTo("lamp"));
    }

    [Test]
    public void Fill_WithoutAManifestStillGivesTheTopicOne() {
      Topic t = Topic.Declare(root, "/dev/x");
      Topic.Fill(t, null, null, null);
      Assert.That((int)t.GetField("attr"), Is.EqualTo(0));
    }

    #endregion Declare + Fill

    #region Move

    [Test]
    public void Move_RenamesAtOnce() {
      Topic t = root.Get("/dev/a");
      Tick();
      ClearEvents();
      t.Move(null, "b");
      Assert.That(t.name, Is.EqualTo("b"));
      Assert.That(t.path, Is.EqualTo("/dev/b"));
      Assert.That(root.Exist("/dev/a"), Is.False);
      Assert.That(root.Get("/dev/b", false), Is.SameAs(t));
    }

    [Test]
    public void Move_IsAnnouncedOnTheTickWithTheOldPath() {
      Topic t = root.Get("/dev/a");
      Tick();
      ClearEvents();
      t.Move(null, "b");
      Assert.That(events, Is.Empty);
      Tick();
      TopicEvent e = EventOf(t, EventKind.Moved);
      Assert.That(e, Is.Not.Null);
      Assert.That(e.OldPath, Is.EqualTo("/dev/a"));
    }

    [Test]
    public void Move_ToAnotherParentUpdatesTheWholeSubtree() {
      Topic b = root.Get("/a/b");
      Topic c = root.Get("/a/b/c");
      Topic d = root.Get("/d");
      b.Move(d, null);
      Assert.That(b.path, Is.EqualTo("/d/b"));
      Assert.That(c.path, Is.EqualTo("/d/b/c"));
      Assert.That(root.Get("/d/b/c", false), Is.SameAs(c));
      Assert.That(root.Exist("/a/b"), Is.False);
    }

    [Test]
    public void Move_RenameAndReparentAtTheSameTime() {
      Topic b = root.Get("/a/b");
      Topic d = root.Get("/d");
      b.Move(d, "z");
      Assert.That(b.path, Is.EqualTo("/d/z"));
      Assert.That(root.Get("/d/z", false), Is.SameAs(b));
    }

    /// <summary>Команда, которая не может сделать то, о чём её просят, говорит об этом.</summary>
    /// <remarks>Молчаливый отказ - худший из трёх возможных ответов: он неотличим от успеха,
    /// и вызывающий продолжает считать, что дерево изменилось.</remarks>
    [Test]
    public void Move_RootIsRefused() {
      Topic a = root.Get("/a");
      Action act = () => root.Move(a, "x");
      Assert.That(act, Throws.InstanceOf<InvalidOperationException>());
    }

    [Test]
    public void Move_RemovedTopicIsRefused() {
      Topic t = root.Get("/a");
      t.Remove();
      Action act = () => t.Move(null, "b");
      Assert.That(act, Throws.InstanceOf<InvalidOperationException>());
    }

    /// <summary>Топик не может стать своим потомком: UpdatePath ушёл бы в бесконечную рекурсию.</summary>
    [Test]
    public void Move_IntoItsOwnSubtreeIsRefused() {
      Topic a = root.Get("/a");
      Topic b = root.Get("/a/b/c");
      Action act = () => a.Move(b, null);
      Assert.That(act, Throws.ArgumentException);
      Assert.That(a.path, Is.EqualTo("/a"));      // дерево осталось нетронутым
      Assert.That(b.path, Is.EqualTo("/a/b/c"));
    }

    [Test]
    public void Move_IntoItselfIsRefused() {
      Topic a = root.Get("/a");
      Action act = () => a.Move(a, null);
      Assert.That(act, Throws.ArgumentException);
    }

    [Test]
    public void Move_OntoATakenNameIsRefused() {
      Topic a = root.Get("/x/a");
      Topic b = root.Get("/x/b");
      Action act = () => a.Move(null, "b");
      Assert.That(act, Throws.ArgumentException);
      Assert.That(a.path, Is.EqualTo("/x/a"));
      Assert.That(root.Get("/x/b", false), Is.SameAs(b));
    }

    [TestCase("a/b")]
    [TestCase("#")]
    [TestCase(" ")]
    public void Move_BadNameIsRefused(string name) {
      Topic a = root.Get("/x/a");
      Action act = () => a.Move(null, name);
      Assert.That(act, Throws.ArgumentException);
      Assert.That(a.path, Is.EqualTo("/x/a"));
    }

    /// <summary>Ни родителя, ни имени: топик остаётся на месте, и порядок детей не портится.</summary>
    [Test]
    public void Move_WithoutArgumentsKeepsThePlace() {
      Topic x = root.Get("/x");
      Topic a = x.Get("a");
      x.Get("b");
      a.Move(null, null);
      Assert.That(a.path, Is.EqualTo("/x/a"));
      Assert.That(root.Get("/x/a", false), Is.SameAs(a));
      Assert.That(Paths(x.children), Is.EqualTo(new[] { "/x/a", "/x/b" }));
    }

    /// <summary>После переименования соседи находятся по-прежнему: массив детей остался упорядоченным.</summary>
    /// <remarks>Двоичный поиск по массиву детей верен только для отсортированного массива, а
    /// переименование - единственная операция, которая переставляет уже существующий элемент.</remarks>
    [Test]
    public void Move_KeepsTheChildrenArraySorted() {
      Topic x = root.Get("/x");
      Topic a = x.Get("a");
      x.Get("c");
      x.Get("e");
      a.Move(null, "d");
      Assert.That(Paths(x.children), Is.EqualTo(new[] { "/x/c", "/x/d", "/x/e" }));
      Assert.That(x.Get("c", false), Is.Not.Null);
      Assert.That(x.Get("e", false), Is.Not.Null);
      Assert.That(x.Get("d", false), Is.SameAs(a));
    }

    #endregion Move

    #region Remove

    [Test]
    public void Remove_MarksAtOnceAndAnnouncesOnTheTick() {
      Topic t = root.Get("/a");
      Tick();
      ClearEvents();
      t.Remove();
      Assert.That(t.disposed, Is.True);
      Assert.That(root.Exist("/a"), Is.False);   // Resolve пропускает удалённые
      Assert.That(events, Is.Empty);
      Tick();
      Assert.That(KindsOf(t), Is.EqualTo(new[] { EventKind.Removed }));
    }

    [Test]
    public void Remove_ClearsTheState() {
      Topic t = root.Get("/a");
      t.SetState(Num(1));
      Tick();
      ClearEvents();
      t.Remove();
      Tick();
      Assert.That(t.GetState().IsNull, Is.True);
    }

    /// <summary>Событие несёт состояние, каким оно было: подписчик узнаёт, что именно исчезло.</summary>
    [Test]
    public void Remove_ReportsTheStateItHad() {
      Topic t = root.Get("/a");
      t.SetState(Num(7));
      Tick();
      ClearEvents();
      t.Remove();
      Tick();
      TopicEvent e = EventOf(t, EventKind.Removed);
      Assert.That(e, Is.Not.Null);
      Assert.That((int)e.OldState, Is.EqualTo(7));
    }

    /// <summary>Удаление разворачивается на всё поддерево: каждый потомок получает своё событие.</summary>
    [Test]
    public void Remove_CascadesToTheWholeSubtree() {
      Topic a = root.Get("/a");
      Topic b = root.Get("/a/b");
      Topic c = root.Get("/a/b/c");
      Tick();
      ClearEvents();
      a.Remove();
      Assert.That(b.disposed, Is.False);   // отмечен сразу только тот, кого удаляли
      Tick();
      Assert.That(b.disposed, Is.True);
      Assert.That(c.disposed, Is.True);
      Assert.That(KindsOf(b), Is.EqualTo(new[] { EventKind.Removed }));
      Assert.That(KindsOf(c), Is.EqualTo(new[] { EventKind.Removed }));
    }

    /// <summary>Между Remove и тиком тот же путь можно запросить снова.</summary>
    /// <remarks>Отвязка убирает пару, а не имя, поэтому отложенное удаление старого топика не
    /// уносит с собой замену, созданную под тем же путём.</remarks>
    [Test]
    public void Remove_ReplacementUnderTheSamePathSurvivesTheUnlink() {
      Topic old = root.Get("/a/x");
      Tick();
      ClearEvents();
      old.Remove();
      Topic fresh = root.Get("/a/x");
      Assert.That(fresh, Is.Not.SameAs(old));
      Assert.That(fresh.disposed, Is.False);
      Tick();
      Assert.That(fresh.disposed, Is.False);
      Assert.That(root.Get("/a/x", false), Is.SameAs(fresh));
    }

    /// <summary>Повторное удаление принимается молча: ни исключения, ни второго события.</summary>
    /// <remarks>Повтор - это задача, уже выполненная, пусть и раньше, поэтому отвечать на него
    /// исключением не за что; этим Remove() отличается от Move(), которому удалённый топик деть
    /// некуда и который его отклоняет. Но и работой повтор не является: об одном удалении подписчику
    /// сообщают один раз.
    /// <para>Вторая половина теста важнее первой: пока топик ждал своего тика, его место мог занять
    /// другой, и повторное удаление не должно задеть занявшего - ни отменой команды, ни, если её
    /// однажды вернут, отсоединением по имени. Unlink снимает пару, а не имя.</para></remarks>
    [Test]
    public void Remove_TwiceInDifferentTicksIsAnnouncedOnce() {
      Topic a = root.Get("/a");
      Tick();
      ClearEvents();

      a.Remove();
      Tick();
      Assert.That(a.disposed, Is.True);
      Assert.That(KindsOf(a), Is.EqualTo(new[] { EventKind.Removed }));

      ClearEvents();
      Topic replacement = root.Get("/a");   // место успел занять другой топик
      a.Remove();
      Tick();
      Assert.That(KindsOf(a), Is.Empty);
      Assert.That(replacement.disposed, Is.False);
      Assert.That(root.Get("/a", false), Is.SameAs(replacement));
    }

    #endregion Remove

    #region Bill

    [Test]
    public void Children_AreEnumeratedInOrdinalOrder() {
      Topic h = root.Get("/h");
      foreach(string n in new[] { "b", "a", "C", "_" }) {
        h.Get(n);
      }
      Assert.That(Paths(h.children), Is.EqualTo(new[] { "/h/C", "/h/_", "/h/a", "/h/b" }));
    }

    [Test]
    public void Children_OfALeafAreEmpty() {
      Assert.That(Paths(root.Get("/h").children), Is.Empty);
    }

    /// <summary>Глубокий обход: родитель раньше своих детей, соседи по возрастанию.</summary>
    [Test]
    public void All_YieldsAParentBeforeItsChildren() {
      Topic r = root.Get("/r");
      r.Get("a/x");
      r.Get("a/y");
      r.Get("b");
      Assert.That(Paths(r.all), Is.EqualTo(new[] { "/r", "/r/a", "/r/a/x", "/r/a/y", "/r/b" }));
    }

    [Test]
    public void Children_SkipARemovedChildAtOnce() {
      Topic h = root.Get("/h");
      Topic a = h.Get("a");
      h.Get("b");
      a.Remove();
      Assert.That(Paths(h.children), Is.EqualTo(new[] { "/h/b" }));
    }

    /// <summary>Глубокий обход выдаёт сам home, даже удалённый: каскаду нужен и он.</summary>
    [Test]
    public void All_YieldsTheHomeEvenWhenItIsRemoved() {
      Topic h = root.Get("/h");
      h.Get("a");
      h.Remove();
      Assert.That(Paths(h.all), Is.EqualTo(new[] { "/h", "/h/a" }));
    }

    [Test]
    public void HasChildren_IgnoresRemovedOnes() {
      Topic h = root.Get("/h");
      Assert.That(h.HasChildren(), Is.False);
      Topic a = h.Get("a");
      Assert.That(h.HasChildren(), Is.True);
      a.Remove();
      Assert.That(h.HasChildren(), Is.False);
    }

    #endregion Bill

    #region IComparable

    [Test]
    public void ToString_IsThePath() {
      Assert.That(root.Get("/a/b").ToString(), Is.EqualTo("/a/b"));
    }

    [Test]
    public void CompareTo_PutsNullFirst() {
      Assert.That(root.Get("/a").CompareTo(null), Is.EqualTo(1));
    }

    [Test]
    public void CompareTo_OrdersByPath() {
      Topic a = root.Get("/a");
      Topic b = root.Get("/b");
      Assert.That(a.CompareTo(b), Is.LessThan(0));
      Assert.That(b.CompareTo(a), Is.GreaterThan(0));
      Assert.That(a.CompareTo(a), Is.Zero);
    }

    #endregion IComparable
  }
}
