///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace X13.Repository {
  /// <summary>Репозиторий: дерево топиков, очередь изменений и тик, который её разбирает.</summary>
  /// <remarks>Плагин с приоритетом 1, потому что до него дерева не существует, загружает конфигурацию из configPath.
  /// Тик разбирает очередь по шести фазам, применяет команды и только после этого публикует события, 
  /// поэтому подписчик видит картину целиком.</remarks>
  [System.ComponentModel.Composition.Export(typeof(IPlugModul))]
  [System.ComponentModel.Composition.ExportMetadata("priority", 1)]
  [System.ComponentModel.Composition.ExportMetadata("name", "Repository")]
  public class Repo : IPlugModul {
    internal static string configPath;
    /// <summary>Количество списков фаз. Вычисляется автоматически, поэтому не может разойтись с Phase.</summary>
    private static readonly int PH_COUNT = Enum.GetValues(typeof(Phase)).Length;
    private const int SAVE_RETRY_SEC = 30;   // после неудачной записи экспорта

    #region internal Members
    private readonly ConcurrentQueue<Cmd> _tcQueue;
    private readonly List<Cmd>[] _phases;              // команды, которые должен выполнить этот тик
    private readonly List<TopicEvent>[] _events;       // полученные события в том же порядке
    private readonly Dictionary<Topic, int> _stateAt;  // топик -> его позиция в фазе состояния
    private readonly Dictionary<FieldKey, int> _fieldAt;   // топик+поле -> его позиция в фазе манифеста
    private volatile Action<TopicEvent>[] _subscribers;  // для SubscribeAll
    private int _busyFlag;
    private DateTime? _saveConfigT;
    private bool _loaded;

    /// <summary>Помещает изменение в очередь. Оно будет применено на следующем тике.</summary>
     internal void DoCmd(Cmd cmd) {
      _tcQueue.Enqueue(cmd);
    }

    /// <summary>Регистрирует callback для всего репозитория и возвращает способ отменить подписку.</summary>
    /// <remarks>Массив заменяется целиком, а не изменяется на месте, поэтому расположенный ниже цикл
    /// публикации читает снимок, который не может измениться во время обхода.</remarks>
    internal IDisposable SubscribeAll(Action<TopicEvent> func) {
      var old = _subscribers;
      var next = new Action<TopicEvent>[old.Length + 1];
      Array.Copy(old, next, old.Length);
      next[old.Length] = func ?? throw new ArgumentNullException("func");
      _subscribers = next;
      return new AllSubRec(this, func);
    }
    private void UnsubscribeAll(Action<TopicEvent> func) {
      var old = _subscribers;
      int idx = Array.IndexOf(old, func);
      if(idx < 0) {
        return;
      }
      var next = new Action<TopicEvent>[old.Length - 1];
      Array.Copy(old, 0, next, 0, idx);
      Array.Copy(old, idx + 1, next, idx, old.Length - idx - 1);
      _subscribers = next;
    }

    /// <summary>Объект, возвращаемый SubscribeAll. Повторный или запоздалый Dispose ничего не делает.</summary>
    private sealed class AllSubRec : IDisposable {
      private Repo _owner;
      private readonly Action<TopicEvent> _func;

      public AllSubRec(Repo owner, Action<TopicEvent> func) {
        _owner = owner;
        _func = func;
      }
      public void Dispose() {
        Repo owner = _owner;
        _owner = null;
        owner?.UnsubscribeAll(_func);
      }
    }
    /// <summary>Помещает команду в соответствующую фазу, объединяя всё, что можно объединить.</summary>
    /// <remarks>Три типа команд не просто добавляются в очередь. Удаление разворачивается на всё
    /// поддерево, поскольку вместе с топиком удаляются все потомки и каждый их подписчик должен
    /// получить уведомление. Запись манифеста объединяется с уже формируемым в этом тике пакетом
    /// для данного топика, но команда остаётся у каждого поля своя. Повторная запись в то же поле,
    /// как и запись состояния, заменяет предыдущую на её месте, поэтому датчик, обновляющийся чаще
    /// тика, создаёт одно событие, а фаза сохраняет порядок первого изменения.</remarks>
    private void Dispatch(Cmd c) {
      if (c is CmdSubscribe sub) {
        Snapshot(sub);
        return;
      }
      if (c is CmdRemove) {
        foreach(Topic tmp in new Topic.Bill(c.Target, true)) {
          _phases[(int)Phase.Remove].Add(new CmdRemove(tmp, c.Author));
        }
        return;
      }
      if (c is CmdField fld) {
        // Регистрируется каждая запись, а не только первая: пакет хранит единый манифест,
        // команды сохраняют собственные пути. Две записи в ОДНО поле объединяются, как для состояния.
        fld.Batch = Topic.SetField(fld);
        List<Cmd> phase = _phases[(int)Phase.Field];
        FieldKey key = new FieldKey(c.Target, fld.Path);
        if (_fieldAt.TryGetValue(key, out int at)) {
          phase[at] = c;   // последняя запись заменяет первую на её позиции
        } else {
          _fieldAt[key] = phase.Count;
          phase.Add(c);
        }
        return;
      }
      if (c is CmdState) {
        List<Cmd> phase = _phases[(int)Phase.State];
        if (_stateAt.TryGetValue(c.Target, out int at)) {
          phase[at] = c;   // последняя запись заменяет первую на её позиции
        } else {
          _stateAt[c.Target] = phase.Count;
          phase.Add(c);
        }
        return;
      }
      _phases[(int)c.Phase].Add(c);
    }

    /// <summary>Состояние для новой подписки: по одному событию на каждый доступный ей топик.</summary>
    /// <remarks>Формируется здесь напрямую, а не применением команды, поскольку одна команда
    /// порождает множество событий. Они относятся к фазе подписки и предшествуют всем остальным
    /// изменениям этого тика, поэтому подписчик не получит изменение топика до сообщения о его
    /// существовании. Подтверждение отправляется последним, только тогда оно имеет смысл.
    /// <para>Снимок самого топика отправляется безусловно, а поддерево отбирается по префиксу:
    /// подписчик, назвавший поле, хочет знать о топике, к которому обратился, независимо от того,
    /// заполнен ли уже его манифест. Фильтруются события, а не снимок.</para>
    /// <para>Глубокий обход начинается с этого же топика, поэтому при Once вместе с All он в обходе
    /// пропускается: иначе подписчик получил бы два события об одном топике.</para></remarks>
    private void Snapshot(CmdSubscribe c) {
      SubRec sr = c.Sub;
      List<TopicEvent> evs = _events[(int)Phase.Sub];
      bool once = (sr.mask & SubRec.SubMask.Once) == SubRec.SubMask.Once;
      if(once) {
        evs.Add(TopicEvent.Snapshot(c.Target, sr));
      }
      Topic.Bill b = null;
      if((sr.mask & SubRec.SubMask.Children) == SubRec.SubMask.Children) {
        b = new Topic.Bill(c.Target, false);
      }
      if((sr.mask & SubRec.SubMask.All) == SubRec.SubMask.All) {
        b = new Topic.Bill(c.Target, true);
      }
      if(b != null) {
        foreach(Topic tmp in b) {
          if(once && object.ReferenceEquals(tmp, c.Target)) {
            continue;   // снимок этого топика уже отправлен ветвью Once
          }
          if((sr.mask & SubRec.SubMask.Value) == SubRec.SubMask.Value
            || (sr.mask & SubRec.SubMask.Field) == SubRec.SubMask.None || string.IsNullOrEmpty(sr.prefix) || tmp.GetField(sr.prefix).Defined) {
            evs.Add(TopicEvent.Snapshot(tmp, sr));
          }
        }
      }
      _events[(int)Phase.Ack].Add(TopicEvent.Ready(c.Target, sr));
    }

    private void PublishSaveConfig(TopicEvent e) {
      if(e.Kind == EventKind.FieldChanged || e.Kind == EventKind.StateChanged || e.Kind == EventKind.Removed) {
        if(e.Source.CheckAttribute(Topic.Attribute.Saved, Topic.Attribute.Config)) {
          _saveConfigT = DateTime.Now.AddSeconds(5);
        }
      }
    }

    #endregion internal Members

    public Repo() {
      _tcQueue = new ConcurrentQueue<Cmd>();
      _phases = new List<Cmd>[PH_COUNT];
      _events = new List<TopicEvent>[PH_COUNT];
      for(int i = 0; i < PH_COUNT; i++) {
        _phases[i] = new List<Cmd>(64);
        _events[i] = new List<TopicEvent>(64);
      }
      _stateAt = new Dictionary<Topic, int>();
      _fieldAt = new Dictionary<FieldKey, int>();
      _subscribers = new Action<TopicEvent>[0];
      _saveConfigT = null;
    }

    #region IPlugModul Members

    public void Init() {
      Topic.Init(this);
      _busyFlag = 1;
      Xst.Import(configPath);   // ничего не делает, если configPath равен null или файл отсутствует
      this.Tick();
      this.Tick();
      _loaded = true;   // намеренно последняя строка: см. Stop
    }

    public void Start() {
      SubscribeAll(PublishSaveConfig);
    }

    /// <summary>Применяет один пакет изменений и публикует полученные события.</summary>
    /// <remarks>Выполняются три прохода по одним и тем же фазам в одинаковом порядке, который и
    /// определяет порядок тика: структура, снимки подписок, манифест, состояние, удаления,
    /// подтверждения. Весь пакет применяется до начала публикации, поэтому подписчик видит
    /// согласованное дерево, а не промежуточное состояние.
    /// <para>Тело завершается через finally, поскольку флаг занятости предотвращает повторный вход
    /// в тик, а его потеря невосстановима.</para>
    /// <para>Кроме того, каждый элемент обрабатывается под собственной защитой: одно неприменимое
    /// изменение не должно привести к потере остального пакета. Фазы очищаются в том же finally,
    /// поэтому частично обработанный пакет не публикуется повторно на следующем тике.</para></remarks>
    public void Tick() {
      if(Interlocked.CompareExchange(ref _busyFlag, 2, 1) != 1) {
        return;
      }
      try {
        while (_tcQueue.TryDequeue(out Cmd cmd)) {
          if (cmd == null || cmd.Target == null) {
            continue;
          }
          try {
            Dispatch(cmd);
          }
          catch (Exception ex) {
            Failed("Dispatch", cmd, ex);
          }
        }

        for (int p = 0; p < PH_COUNT; p++) {
          List<Cmd> phase = _phases[p];
          List<TopicEvent> evs = _events[p];
          for(int i = 0; i < phase.Count; i++) {
            try {
              TopicEvent e = phase[i].Apply();
              if(e != null) {
                evs.Add(e);
              }
            }
            catch(Exception ex) {
              Failed("Apply", phase[i], ex);
            }
          }
        }

        for(int p = 0; p < PH_COUNT; p++) {
          List<TopicEvent> evs = _events[p];
          for(int i = 0; i < evs.Count; i++) {
            TopicEvent e = evs[i];
            try {
              Topic.Publish(e);
            }
            catch(Exception ex) {
              Failed("Publish", e, ex);
            }
            // Поле читается один раз, после чего обходится полученный массив: callback может отменить
            // свою или чужую подписку, и цикл не должен обходить изменяемый им список.
            var subs = _subscribers;
            for(int k = subs.Length-1; k>=0; k--) {
              var func = subs[k];
              try {
                func.Invoke(e);
              }
              catch(Exception ex) {
                PluginFailed((func.Target != null ? func.Target.ToString() : func.Method.DeclaringType.Name) + "." + func.Method.Name, e, ex);
              }
            }
          }
        }
        if(_saveConfigT!=null && _saveConfigT<DateTime.Now) {
          _saveConfigT=null;
          try {
            Xst.Export(configPath, Topic.root, true);
          }
          catch(Exception ex) {
            // Запрос повторяется, а не отбрасывается.
            _saveConfigT = DateTime.Now.AddSeconds(SAVE_RETRY_SEC);
            Failed("Export", null, ex);
          }
        }
        _faults.Flush(DateTime.Now);
      }
      finally {
        for(int p = 0; p < PH_COUNT; p++) {
          _phases[p].Clear();
          _events[p].Clear();
        }
        _stateAt.Clear();
        _fieldAt.Clear();
        _busyFlag = 1;
      }
    }

    /// <summary>Ошибка во внутренней работе репозитория: тик перехватил её и продолжил выполнение.</summary>
    internal void Failed(string where, object subject, Exception ex) {
      _faults.Report(true, "Repo." + where, subject, ex);
    }

    /// <summary>Ошибка в чужом callback: конструкторе типа, подписчике и т. п.</summary>
    /// <remarks>К моменту доставки дерево уже согласовано; исключение определяет только того, кто
    /// не получит уведомление. Поэтому это предупреждение, а не ошибка, и остальные подписчики
    /// продолжают получать события.</remarks>
    internal void PluginFailed(string who, object subject, Exception ex) {
      _faults.Report(false, who, subject, ex);
    }
    private readonly FaultThrottle _faults = new FaultThrottle();
    /// <summary>Одно поле одного топика — ключ объединения записей манифеста в пределах тика.</summary>
    /// <remarks>Топик сравнивается по ссылке, путь — с использованием Ordinal. Два экземпляра Topic
    /// никогда не равны друг другу, а путь поля является именем, а не текстом для нечёткого
    /// сравнения.</remarks>
    private struct FieldKey : IEquatable<FieldKey> {
      private readonly Topic _topic;
      private readonly string _path;

      public FieldKey(Topic topic, string path) {
        _topic = topic;
        _path = path;
      }
      public bool Equals(FieldKey other) {
        return object.ReferenceEquals(_topic, other._topic) && string.Equals(_path, other._path, StringComparison.Ordinal);
      }
      public override bool Equals(object obj) {
        return obj is FieldKey && Equals((FieldKey)obj);
      }
      public override int GetHashCode() {
        return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(_topic) ^ (_path == null ? 0 : _path.GetHashCode());
      }
    }

    /// <summary>Сохраняет конфигурацию, но никогда не записывает дерево, прочитанное не полностью.</summary>
    /// <remarks>Import выбрасывает исключение для обрезанного или повреждённого server.xst, что как
    /// раз возможно после отключения питания во время предыдущего Export. Запуск завершается
    /// ошибкой, и сервер останавливается. 
    /// <para>Флаг устанавливается только Init и только его последней инструкцией, поэтому loaded
    /// означает полное завершение Topic.Init, импорта и обоих тиков.</para></remarks>
    public void Stop() {
      if(!_loaded) {
        Log.Warning("Repository did not finish loading; {0} is left as it is", configPath);
        return;
      }
      Xst.Export(configPath, Topic.root, true);
    }

    /// <summary>Топик собственных настроек репозитория. До первого обращения ничего не создаётся.</summary>
    public Topic Owner { get { return _owner ?? (_owner = Topic.root.Get(OWNER_PATH, true)); } }
    private const string OWNER_PATH = "/$YS/Repository";
    private Topic _owner;

    /// <summary>Всегда включён — единственный плагин, который не читает это значение из топика Owner.</summary>
    public bool enabled { get { return true; } }
    #endregion IPlugModul Members
  }
}
