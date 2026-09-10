///<remarks>Этот файл является частью проекта <see cref="https://github.com/enviriot">Enviriot</see>.<remarks>
using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using X13.Repository;
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;

// Дерево топиков - глобальное состояние процесса: Topic.root и Topic._repo статические, а
// Topic.Init(repo) не добавляет ещё одно дерево, а заменяет единственное. Два теста, идущие
// одновременно, работали бы с одним корнем и переустанавливали бы его друг у друга, поэтому
// параллельный запуск запрещён явно, а не оставлен на умолчание NUnit.
[assembly: NonParallelizable]

namespace X13.Tests {
  /// <summary>Общая обвязка тестов репозитория: своё дерево на каждый тест и тик вручную.</summary>
  /// <remarks>Repo.Init() внутри вызывает Topic.Init(this), поэтому каждый тест начинается с
  /// пустого корня и не видит топиков, созданных соседним. Отдельного способа получить дерево нет:
  /// Tick() ничего не делает, пока Init() не поднял _busyFlag с нуля.
  /// <para>Start() намеренно не вызывается. Он подписывает PublishSaveConfig, и первая же запись в
  /// Config-топик привела бы к Xst.Export(configPath, ...) прямо внутри тика - то есть к обращению
  /// к файловой системе, а при configPath == null к исключению, которое тик проглотит и запишет в
  /// журнал. Ни того, ни другого тесту не нужно.</para>
  /// <para>События собираются через Topic.Subscribe - подписку на весь репозиторий. Здесь она
  /// инструмент, а не предмет проверки: тесту нужно отличить "изменение применено" от "изменение
  /// опубликовано", потому что состояние, манифест и все события откладываются до тика.</para></remarks>
  public abstract class RepoTestBase {
    private Repo _repo;
    private IDisposable _sub;

    /// <summary>Всё, что репозиторий опубликовал с начала теста, в порядке публикации.</summary>
    protected readonly List<TopicEvent> events = new List<TopicEvent>();

    protected static Topic root { get { return Topic.root; } }

    [OneTimeSetUp]
    public void RepoTestBaseOneTimeSetUp() {
      // Контекст NiL.JS привязан к потоку, а JsLib.ParseJson и JsLib.Clone обращаются к нему.
      JsExtLib.ActivateEngineOnThisThread();
      // Каталог ../log статический конструктор Log всё равно создаст; это отключает запись в файл.
      Log.useFile = false;
    }

    [SetUp]
    public void RepoTestBaseSetUp() {
      Repo.configPath = null;   // ни импорт при Init, ни авто-сохранение не трогают файловую систему
      _repo = new Repo();
      _repo.Init();
      events.Clear();
      _sub = Topic.Subscribe(events.Add);
    }

    [TearDown]
    public void RepoTestBaseTearDown() {
      if(_sub != null) {
        _sub.Dispose();
        _sub = null;
      }
      events.Clear();
    }

    /// <summary>Применяет накопленные команды и публикует полученные события.</summary>
    protected void Tick() {
      _repo.Tick();
    }
    protected void ClearEvents() {
      events.Clear();
    }
    /// <summary>Виды событий, опубликованных для одного топика, в порядке публикации.</summary>
    protected EventKind[] KindsOf(Topic t) {
      return events.Where(e => e.Source == t).Select(e => e.Kind).ToArray();
    }
    /// <summary>Событие указанного вида для топика, или null.</summary>
    protected TopicEvent EventOf(Topic t, EventKind kind) {
      return events.FirstOrDefault(e => e.Source == t && e.Kind == kind);
    }
    /// <summary>Пути топиков в перечислении - то, что удобно сравнивать с ожидаемым списком.</summary>
    protected static string[] Paths(IEnumerable<Topic> src) {
      return src.Select(t => t.path).ToArray();
    }

    protected static JSC.JSValue Num(double v) { return new JSL.Number(v); }
    protected static JSC.JSValue Str(string v) { return new JSL.String(v); }
  }
}
