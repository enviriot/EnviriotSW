///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using X13.Repository;
using JSC = NiL.JS.Core;

namespace X13.LogramPLC {
  /// <summary>Сравнивает результат последнего Build/Run с подтверждённой загрузкой без компиляции.</summary>
  /// <remarks>Нет результата после запуска — сравнение неизвестно. Правки требуют нового Build/Run;
  /// возврат к входным данным сборки позволяет использовать её результат. Наблюдатель не заменяет LPLC.run.</remarks>
  internal static class LP_Status {
    internal const string ModifiedField = "LPLC.modified";
    internal const string LoadedField = "LPLC.loaded";
    private sealed class Built {
      internal string Inputs;
      internal string Fingerprint;
    }
    private static readonly ConditionalWeakTable<Topic, Built> _built = new ConditionalWeakTable<Topic, Built>();

    internal static void RecordBuilt(Topic program, string fingerprint) {
      var result = _built.GetValue(program, t => new Built());
      result.Inputs = Inputs(program);
      result.Fingerprint = fingerprint;
      Refresh(program);
    }

    internal static void Refresh(Topic program) {
      if(!LP_Schema.IsPa1(program) || program.disposed) return;
      string loaded = program.GetField(LoadedField).AsString(null);
      Built built;
      bool? modified = string.IsNullOrEmpty(loaded) || !_built.TryGetValue(program, out built) ? (bool?)null
        : built.Fingerprint == null || loaded != built.Fingerprint || built.Inputs != Inputs(program);
      JSC.JSValue current = program.GetField(ModifiedField);
      if(!modified.HasValue) {
        if(current.Defined) program.SetField(ModifiedField, JSC.JSValue.Null, program);
      }
      else if(current.ValueType != JSC.JSValueType.Boolean || (bool)current != modified.Value) {
        program.SetField(ModifiedField, modified.Value, program);
      }
    }

    // Читается входная модель компилятора, а не сырые манифесты: расположение, видимость пинов,
    // текущие значения, назначенные адреса меркеров и кэш исполнения не являются входами программы.
    private static string Inputs(Topic program) {
      var diagram = LP_Tree.Read(program);
      var key = new StringBuilder();
      Action<string> add = value => key.Append(value == null ? -1 : value.Length).Append(':').Append(value);
      add(diagram.Blocks.Count.ToString());
      foreach(var block in diagram.Blocks) {
        foreach(string value in new[] { block.Name, block.Kind.ToString(), block.Src, block.Pin, block.Mode,
          block.Option, block.Expr, block.Op }) add(value);
        add(block.Inputs == null ? null : block.Inputs.Length.ToString());
        if(block.Inputs != null) foreach(string input in block.Inputs) add(input);
        add(block.Arguments == null ? null : string.Join("\n", block.Arguments));
        add(block.Configuration == null ? null : JsLib.Stringify(block.Configuration.Values));
      }
      add(diagram.Variables.Count.ToString());
      foreach(var variable in diagram.Variables) {
        add(variable.Name);
        add(variable.Type.ToString());
      }
      add(diagram.Links.Count.ToString());
      foreach(var link in diagram.Links) {
        add(link.From);
        add(link.Const);
        add(link.To);
      }
      string type = program.parent?.GetField("type").AsString(null);
      var definition = type == null ? null : Topic.root.Get("/$YS/TYPES/" + type, false);
      add(definition == null ? null : JsLib.Stringify(definition.GetState().Field("PLC")));
      return key.ToString();
    }

    // Замена родительского объекта тоже может изменить поле, например запись LPLC или MQTT-SN.
    private static bool Affects(string path, string field) {
      return string.IsNullOrEmpty(path) || path == field
        || path.StartsWith(field + ".", StringComparison.Ordinal)
        || field.StartsWith(path + ".", StringComparison.Ordinal);
    }

    /// <summary>Объединяет проверки метаданных типизированных программ, в том числе без подключения; не компилирует.</summary>
    internal sealed class Watcher : IDisposable {
      private readonly object _sync = new object();
      private readonly Dictionary<Topic, Tracker> _programs = new Dictionary<Topic, Tracker>();
      private SubRec _root;
      private SubRec _types;

      internal Watcher(Topic root) {
        _root = root.Subscribe(SubRec.SubMask.All | SubRec.SubMask.Field, "type", RootEvent);
        _types = root.Get("/$YS/TYPES", true).Subscribe(SubRec.SubMask.All | SubRec.SubMask.Value, TypesEvent);
      }

      private void RootEvent(TopicEvent e, SubRec sr) {
        switch(e.Kind) {
        case EventKind.Snapshot:
        case EventKind.Created:
        case EventKind.FieldChanged:
        case EventKind.Moved:
        case EventKind.Removed:
          break;
        default:
          return;
        }
        Topic program = e.Source;
        if(program == null) return;
        Tracker removed = null;
        lock(_sync) {
          if(_root == null) return;
          Tracker tracker;
          _programs.TryGetValue(program, out tracker);
          if(e.Kind == EventKind.Removed || program.disposed || !LP_Schema.IsPa1(program)) {
            _built.Remove(program);
            if(tracker != null) {
              _programs.Remove(program);
              removed = tracker;
            }
          }
          else {
            if(tracker == null) _programs[program] = tracker = new Tracker(program);
            tracker.FollowDevice();
            tracker.Invalidate();
          }
        }
        removed?.Dispose();
      }

      private void TypesEvent(TopicEvent e, SubRec sr) {
        if(e.Kind != EventKind.StateChanged && e.Kind != EventKind.Created
          && e.Kind != EventKind.Removed && e.Kind != EventKind.Moved) return;
        lock(_sync) {
          foreach(Tracker tracker in _programs.Values) tracker.Invalidate();
        }
      }

      internal void Tick() {
        Tracker[] programs;
        lock(_sync) programs = _programs.Values.ToArray();
        foreach(Tracker tracker in programs) tracker.Check();
      }

      public void Dispose() {
        Tracker[] programs;
        lock(_sync) {
          _root?.Dispose();
          _root = null;
          _types?.Dispose();
          _types = null;
          programs = _programs.Values.ToArray();
          _programs.Clear();
        }
        foreach(Tracker tracker in programs) tracker.Dispose();
      }
    }

    private sealed class Tracker : IDisposable {
      private readonly Topic _program;
      private readonly SubRec _programSub;
      private Topic _device;
      private SubRec _deviceSub;
      private int _dirty = 1;
      private bool _disposed;

      internal Tracker(Topic program) {
        _program = program;
        _programSub = program.Subscribe(SubRec.SubMask.Once | SubRec.SubMask.Children | SubRec.SubMask.Value | SubRec.SubMask.Field, ProgramEvent);
        FollowDevice();
      }

      internal void FollowDevice() {
        if(_disposed || _device == _program.parent) return;
        _deviceSub?.Dispose();
        _device = _program.parent;
        _deviceSub = _device?.Subscribe(SubRec.SubMask.Once | SubRec.SubMask.Children | SubRec.SubMask.Field, DeviceEvent);
        Invalidate();
      }

      internal void Invalidate() {
        Interlocked.Exchange(ref _dirty, 1);
      }

      private void ProgramEvent(TopicEvent e, SubRec sr) {
        if(_disposed) return;
        if(e.Kind == EventKind.Created || e.Kind == EventKind.Removed || e.Kind == EventKind.Moved) {
          FollowDevice();
          Invalidate();
        }
        else if(e.Kind == EventKind.StateChanged && e.Source != _program
          && X13.Plc.PlcScript.UsesState(e.Source.GetField("type").AsString(null))) {
          Invalidate();
        }
        else if(e.Kind == EventKind.FieldChanged) {
          bool relevant = e.Source == _program
            ? Affects(e.FieldPath, LoadedField) || Affects(e.FieldPath, "type")
            : Affects(e.FieldPath, "type") || Affects(e.FieldPath, "LPLC.in") || Affects(e.FieldPath, "LPLC.args");
          if(relevant) Invalidate();
        }
      }

      private void DeviceEvent(TopicEvent e, SubRec sr) {
        if(_disposed) return;
        if(e.Kind == EventKind.Created || e.Kind == EventKind.Removed || e.Kind == EventKind.Moved) {
          Invalidate();
        }
        else if(e.Kind == EventKind.FieldChanged) {
          bool relevant = e.Source == _device ? Affects(e.FieldPath, "type")
            : Affects(e.FieldPath, "MQTT-SN.tag") || Affects(e.FieldPath, "LPLC.in");
          if(relevant) Invalidate();
        }
      }

      internal void Check() {
        if(_disposed || Interlocked.Exchange(ref _dirty, 0) == 0 || _program.disposed) return;
        try {
          Refresh(_program);
        }
        catch(Exception ex) {
          // Нечитаемые входные данные не должны оставлять закэшированный признак совпадения.
          _program.SetField(ModifiedField, JSC.JSValue.Null, _program);
          Log.Warning("LPLC status {0}: {1}", _program.path, ex.Message);
        }
      }

      public void Dispose() {
        _disposed = true;
        _programSub.Dispose();
        _deviceSub?.Dispose();
        _deviceSub = null;
      }
    }
  }
}
