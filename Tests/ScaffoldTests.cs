///<remarks>Этот файл является частью проекта <see cref="https://github.com/enviriot">Enviriot</see>.<remarks>
using NUnit.Framework;

namespace X13.Tests {
  /// <summary>Проверка обвязки, а не кода: проект собирается, адаптер находит тест, internal видны.</summary>
  /// <remarks>Пустой тестовый проект vstest.console считает ошибкой прогона ("не найдено ни одного
  /// теста"), поэтому здесь всегда должен оставаться хотя бы один тест. Заодно он держит проверку,
  /// которую больше негде поставить: [assembly: InternalsVisibleTo("X13.Tests")] в шести проектах
  /// работает только пока сборка называется X13.Tests, и обращения к internal-типам ниже перестанут
  /// компилироваться в тот же момент, когда имя поменяют.
  /// <para>Настоящие тесты кладутся рядом отдельными файлами - этот удалять не нужно.</para></remarks>
  [TestFixture]
  public class ScaffoldTests {
    [Test]
    public void InternalsAreVisible() {
      // Проверяет по существу компилятор, а не эти две строки: до Assert дело дойдёт только
      // если обращения к internal-типам вообще скомпилировались.
      Assert.That(typeof(X13.FaultThrottle), Is.Not.Null);            // internal в Server
      Assert.That(typeof(X13.WebUI.LogramWireRouter), Is.Not.Null);   // internal в WebUI
    }
  }
}
