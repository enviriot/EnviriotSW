///<remarks>Этот файл является частью проекта <see cref="https://github.com/enviriot">Enviriot</see>.<remarks>
using System;
using NUnit.Framework;
using X13.Repository;

namespace X13.Tests {
  /// <summary>Правило имени топика: IsValidName, CheckName, CheckPath.</summary>
  /// <remarks>Единственное место, где это правило записано, и раньше копий было четыре: Resolve
  /// отвергал подстановочные знаки, Move не проверял ничего, импорт .xst завёл собственную
  /// проверку, а WebUI - четвёртую. Тесты держат обе стороны: и что именно запрещено, и что
  /// разрешённое не отвергается заодно.
  /// <para>Дерева здесь не нужно: все три метода статические и ничего не читают.</para></remarks>
  [TestFixture]
  public class TopicNameTests {

    [TestCase("dev")]
    [TestCase("light1")]
    [TestCase("$YS")]
    [TestCase("A")]
    [TestCase("_")]
    [TestCase("привет")]
    public void IsValidName_PlainName(string name) {
      Assert.That(Topic.IsValidName(name), Is.True);
    }

    /// <summary>Точка разделяет поля манифеста, а не сегменты пути, и в имени допустима.</summary>
    [TestCase("a.b")]
    [TestCase("file.txt")]
    public void IsValidName_DotIsNotASeparator(string name) {
      Assert.That(Topic.IsValidName(name), Is.True);
    }

    /// <summary>Запрещены только сами подстановочные знаки, а не имена, которые их содержат.</summary>
    [TestCase("#x")]
    [TestCase("x#")]
    [TestCase("+1")]
    [TestCase("a+b")]
    public void IsValidName_WildcardInsideNameIsAllowed(string name) {
      Assert.That(Topic.IsValidName(name), Is.True);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase(" ")]
    [TestCase("\t")]
    [TestCase("   ")]
    public void IsValidName_BlankIsRefused(string name) {
      // Пробельное имя пропускали короткие копии проверки: топик " " создавался, а переименовать
      // что-либо в " " Move уже отказывался - две половины одной операции расходились.
      Assert.That(Topic.IsValidName(name), Is.False);
    }

    [TestCase("a/b")]
    [TestCase("/a")]
    [TestCase("a/")]
    [TestCase("/")]
    public void IsValidName_SeparatorInsideNameIsRefused(string name) {
      // Move взял бы такое имя ключом словаря дословно, и получился бы топик, до которого не
      // добирается ни один поиск по пути.
      Assert.That(Topic.IsValidName(name), Is.False);
    }

    [TestCase("#")]
    [TestCase("+")]
    public void IsValidName_WildcardAloneIsRefused(string name) {
      Assert.That(Topic.IsValidName(name), Is.False);
    }

    [Test]
    public void CheckName_ValidNamePasses() {
      Action act = () => Topic.CheckName("dev", "ctx");
      Assert.That(act, Throws.Nothing);
    }

    [Test]
    public void CheckName_BadNameThrowsWithContextAndValue() {
      Action act = () => Topic.CheckName("a/b", "Move");
      var ex = Assert.Throws<ArgumentException>(act);
      Assert.That(ex.Message, Does.Contain("Move"));
      Assert.That(ex.Message, Does.Contain("a/b"));
    }

    [Test]
    public void CheckName_NullIsNamedInTheMessage() {
      Action act = () => Topic.CheckName(null, "Move");
      var ex = Assert.Throws<ArgumentException>(act);
      Assert.That(ex.Message, Does.Contain("<null>"));
    }

    /// <summary>Путь - не имя: он может быть "/" и состоять из нескольких сегментов.</summary>
    [TestCase(null)]
    [TestCase("")]
    [TestCase("/")]
    [TestCase("dev")]
    [TestCase("/dev/light1")]
    [TestCase("//dev///light1//")]
    public void CheckPath_Passes(string path) {
      Action act = () => Topic.CheckPath(path, "Import");
      Assert.That(act, Throws.Nothing);
    }

    /// <summary>Каждый уцелевший сегмент проверяется правилом целиком, а не его частью.</summary>
    /// <remarks>Пустой сегмент RemoveEmptyEntries отбрасывает, пробельный - нет. Импорт .xst
    /// принимал пробельный сегмент в пути, которому адресован, и двумя строками ниже отвергал
    /// такое же пробельное имя ребёнка.</remarks>
    [TestCase("/dev/#/x")]
    [TestCase("/dev/+")]
    [TestCase("/ /x")]
    [TestCase("#")]
    public void CheckPath_BadSegmentThrows(string path) {
      Action act = () => Topic.CheckPath(path, "Import");
      Assert.That(act, Throws.ArgumentException);
    }
  }
}
