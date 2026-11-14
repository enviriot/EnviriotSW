///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using X13.Repository;
using X13.WebUI.Helpers;

namespace X13.WebUI {
  /// <summary>Программа LPLC (pa1) для браузера: раскладка блоков и переменных, их pin-ы и провода.</summary>
  /// <remarks>Строки и сетка те же, что у LogramGraphController, поэтому обе схемы рисует один logram-scene.js:
  /// строка канвы на программу, LogramBlock на блок, LogramVariable на переменную устройства, LogramPin на
  /// каждый показанный pin.
  /// <para>Pin (LPLC/Pin) рисуется как переменная: LogramVariable со своим именем, без иконки и строк pin-ов;
  /// её вход - LPLC.in.x, её выход - value, провод от неё идёт из самой строки.</para>
  /// <para>Pin - не топик: вход - ключ LPLC.in блока, выход - ключ LPLC.out, поэтому их vid виртуальные
  /// (LplcModel.PinVid), и источник провода опознаётся по vid, а не по Topic. Клиент группирует pin-ы по
  /// части vid до последнего '/', то есть по vid блока.</para>
  /// <para>Переменные - дети устройства, соседи программы, поэтому подписка двойная: на программу целиком
  /// и на детей устройства.</para>
  /// <para>Trace выхода читает переменную-наблюдатель устройства, не рисуя её отдельным элементом.
  /// Константа на входе показывается подписью pin-а.</para></remarks>
  internal sealed class LplcGraphController : IDisposable {
    private readonly Action<JSC.JSObject> _send;
    private readonly Action<string, Action> _post;
    private readonly Topic _program;
    private readonly Action<LplcGraphController> _onRootGone;
    private Topic _device;
    private SubRec _sub;
    private SubRec _deviceSub;
    private SubRec _typesSub;
    // Same single-thread model as LogramGraphController: every entry arrives through the session queue.
    private bool _disposed;
    private bool _snapshotDirty;
    private bool _sending;
    // State and synchronization status can change without changing any diagram element.
    private string _canvasFingerprint;
    private readonly HashSet<string> _knownVids = new HashSet<string>(StringComparer.Ordinal);
    // Row and route as last sent, by vid. An evnt.add replaces the client's row and drops the route
    // fields with it, so re-sending a row forgets its route (see MarkRowSent).
    private readonly Dictionary<string, string> _rowFingerprint = new Dictionary<string, string>(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _wireFingerprint = new Dictionary<string, string>(StringComparer.Ordinal);
    private readonly Dictionary<string, Topic> _traceSources = new Dictionary<string, Topic>(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _traceFingerprint = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <param name="post">Queues work for the engine thread; null means "run it here, now".</param>
    internal LplcGraphController(Action<JSC.JSObject> send, Topic program, Action<LplcGraphController> onRootGone = null, Action<string, Action> post = null) {
      _post = post ?? ((what, work) => work());
      _send = send;
      _program = program;
      _onRootGone = onRootGone;
    }

    internal string RootVid { get { return LplcModel.Vid(_program); } }
    /// <summary>The program this controller renders - what LplcViewProvider keys its documents by.</summary>
    internal Topic Root { get { return _program; } }

    internal void Open() {
      _sub = _program.Subscribe(SubRec.SubMask.All | SubRec.SubMask.Field | SubRec.SubMask.Value, OnChanged);
      _device = _program.parent;
      if(_device != null) {
        // Once - the device's own fields ("Children": the variable templates), Children - its variables.
        _deviceSub = _device.Subscribe(SubRec.SubMask.Once | SubRec.SubMask.Children | SubRec.SubMask.Field | SubRec.SubMask.Value, OnChanged);
      }
      var types = Topic.root.Get("/$YS/TYPES", false);
      if(types != null) _typesSub = types.Subscribe(SubRec.SubMask.All | SubRec.SubMask.Field | SubRec.SubMask.Value, OnTypesChanged);
      SendSnapshot();
    }

    public void Dispose() {
      _disposed = true;
      _sub?.Dispose();
      _sub = null;
      _deviceSub?.Dispose();
      _deviceSub = null;
      _typesSub?.Dispose();
      _typesSub = null;
    }

    private void OnTypesChanged(TopicEvent p, SubRec sub) {
      if(p == null || p.Source == null) return;
      string deviceType = _device == null ? null : _device.GetField("type").AsString(null);
      if(p.Source.path == "/$YS/TYPES/" + deviceType || LplcModel.Blocks(_program).Any(b => p.Source.path == "/$YS/TYPES/" + b.GetField("type").AsString(null)))
        _post("constructor schema changed", () => { if(!_disposed) ScheduleSnapshot(); });
    }

    private void OnChanged(TopicEvent p, SubRec sub) {
      _post("callback " + (p == null || p.Source == null ? RootVid : p.Source.path), () => OnChangedCore(p, sub));
    }

    private void OnChangedCore(TopicEvent p, SubRec sub) {
      try {
        if(_disposed || p == null || p.Source == null) return;
        if(p.Kind == EventKind.Removed && p.Source == _program) {
          HandleRootRemoved();
          return;
        }
        if(p.Kind == EventKind.StateChanged && p.Source != _program) {
          foreach(var pair in new List<KeyValuePair<string, Topic>>(_traceSources)) {
            if(pair.Value == p.Source) UpdateTrace(pair.Key, pair.Value);
          }
          return;
        }
        // Snapshot/Ready answer the subscriptions themselves - Open() already sent the snapshot.
        if(p.Kind == EventKind.Created || p.Kind == EventKind.Removed || p.Kind == EventKind.Moved
          || (p.Kind == EventKind.StateChanged && p.Source == _program)
          || (p.Kind == EventKind.FieldChanged && !IsBookkeeping(p.FieldPath))) {
          ScheduleSnapshot();
        }
      }
      catch(Exception ex) {
        Log.Warning("LplcGraphController({0}).OnChanged - {1}", _program.path, ex.ToString());
      }
    }

    // What MQTT_SN writes on every compile and load - never anything drawn.
    private static bool IsBookkeeping(string fieldPath) {
      return IsField(fieldPath, "LPLC.run") || IsField(fieldPath, "LPLC.loaded");
    }

    private static bool IsField(string fieldPath, string field) {
      return fieldPath != null && fieldPath.StartsWith(field, StringComparison.Ordinal)
        && (fieldPath.Length == field.Length || fieldPath[field.Length] == '.');
    }

    private void HandleRootRemoved() {
      string vid = RootVid;
      _disposed = true;
      _send(ViewProtocolSerializer.Del(vid));
      _sub?.Dispose();
      _sub = null;
      _deviceSub?.Dispose();
      _deviceSub = null;
      if(_onRootGone != null) _onRootGone(this);
      _typesSub?.Dispose();
      _typesSub = null;
    }

    // Coalesced as LogramGraphController.ScheduleSnapshot: the flush lands on the next pump pass.
    private void ScheduleSnapshot() {
      if(_disposed) return;
      _snapshotDirty = true;
      _post("snapshot " + RootVid, FlushSnapshot);
    }

    private void FlushSnapshot() {
      if(_disposed || !_snapshotDirty) return;
      _snapshotDirty = false;
      SendSnapshot();
    }

    private void SendSnapshot() {
      if(_disposed || _sending) return;
      _sending = true;
      try {
        SendSnapshotCore();
      }
      finally {
        _sending = false;
      }
    }

    private void MarkRowSent(string vid, string fingerprint) {
      _rowFingerprint[vid] = fingerprint;
      _wireFingerprint.Remove(vid);
    }

    private void SendSnapshotCore() {
      string rootVid = RootVid;
      List<ElementLayout> layouts = BuildLayouts();

      HashSet<string> currentVids = new HashSet<string>(StringComparer.Ordinal);
      currentVids.Add(rootVid);
      foreach(ElementLayout layout in layouts) {
        currentVids.Add(layout.Vid);
        foreach(PinLayout pin in layout.Pins) currentVids.Add(pin.Vid);
      }

      // Deletions before additions - see LogramGraphController.SendSnapshotCore (rename case).
      foreach(string staleVid in _knownVids) {
        if(!currentVids.Contains(staleVid)) _send(ViewProtocolSerializer.Del(staleVid));
      }

      JSC.JSObject canvas = SerializeCanvasRow();
      string canvasFingerprint = JsLib.Stringify(canvas);
      if(_canvasFingerprint != canvasFingerprint) {
        _send(canvas);
        _canvasFingerprint = canvasFingerprint;
      }

      foreach(ElementLayout layout in layouts) {
        string fingerprint = ElementFingerprint(layout);
        string previous;
        if(!_rowFingerprint.TryGetValue(layout.Vid, out previous) || previous != fingerprint) {
          _send(SerializeElementRow(layout));
          MarkRowSent(layout.Vid, fingerprint);
        }
        if(layout.IsPin) UpdateTrace(layout.Vid, layout.Observer);
        foreach(PinLayout pin in layout.Pins) {
          string pinFingerprint = PinFingerprint(pin);
          if(!_rowFingerprint.TryGetValue(pin.Vid, out previous) || previous != pinFingerprint) {
            _send(SerializePinRow(pin));
            MarkRowSent(pin.Vid, pinFingerprint);
          }
          if(!pin.Input) UpdateTrace(pin.Vid, pin.Observer);
        }
      }

      _knownVids.Clear();
      foreach(string vid in currentVids) _knownVids.Add(vid);
      Prune(_rowFingerprint, currentVids);
      Prune(_wireFingerprint, currentVids);
      Prune(_traceFingerprint, currentVids);
      foreach(string vid in new List<string>(_traceSources.Keys)) {
        if(!currentVids.Contains(vid)) _traceSources.Remove(vid);
      }

      ResolveWires(layouts);
    }

    private void WriteTrace(JSC.JSObject dto, string vid, Topic observer) {
      _traceSources[vid] = observer;
      bool traced = observer != null && !observer.disposed && LplcModel.IsObserver(observer)
        && observer.GetField(LplcModel.TraceField).AsBool(false);
      string value = traced ? LogramGraphController.FormatTraceValue(RowProjector.ToWebStateValue(observer.GetState())) : string.Empty;
      dto["trace"] = traced;
      dto["displayValue"] = value;
      _traceFingerprint[vid] = (traced ? "1|" : "0|") + value;
    }

    private void UpdateTrace(string vid, Topic observer) {
      string previous;
      _traceFingerprint.TryGetValue(vid, out previous);
      var dto = ViewProtocolSerializer.RowBase(ViewMessageTypes.EvntUpd, vid);
      WriteTrace(dto, vid, observer);
      if(previous != _traceFingerprint[vid]) _send(dto);
    }

    private static void Prune(Dictionary<string, string> sent, HashSet<string> currentVids) {
      List<string> stale = new List<string>();
      foreach(string vid in sent.Keys) {
        if(!currentVids.Contains(vid)) stale.Add(vid);
      }
      foreach(string vid in stale) sent.Remove(vid);
    }

    private static string ElementFingerprint(ElementLayout layout) {
      return layout.X + "|" + layout.Y + "|" + layout.WidthCells + "|" + layout.HeightCells
        + "|" + EditorOf(layout)
        + "|" + IconOf(layout)
        + "|" + (layout.Topic.name ?? string.Empty)
        + "|" + JsLib.Stringify(ConstructorArray(layout))
        + "|" + (layout.ConstructorRows == null ? string.Empty : JsLib.Stringify(layout.ConstructorRows)) + "|" + layout.ConfigurationError;
    }

    private static string EditorOf(ElementLayout layout) {
      return layout.IsBlock && !layout.IsPin ? LogramGraphController.EditorBlock : LogramGraphController.EditorVariable;
    }

    // A variable row is its name alone - a Pin too.
    private static string IconOf(ElementLayout layout) {
      return layout.IsPin ? string.Empty : (IconResource.Resolve(layout.Topic, null) ?? string.Empty);
    }

    private static string PinFingerprint(PinLayout pin) {
      return (pin.Input ? "in" : "out") + "|" + pin.Index + "|" + pin.Name + "|" + (pin.Label ?? "\u0001") + "|" + pin.Error + "|" + pin.ConstantEditable;
    }

    // === Layout ===

    // Blocks first, then variables, each in name order: WireRoutingPass claims accumulate in this
    // order, so it has to be the same on every pass.
    private List<ElementLayout> BuildLayouts() {
      List<Topic> blocks = LplcModel.Blocks(_program);
      List<Topic> variables = LplcModel.Variables(_program);
      Dictionary<string, List<string>> referenced = LplcModel.ReferencedOutputs(blocks, variables);
      List<ElementLayout> layouts = new List<ElementLayout>(blocks.Count + variables.Count);
      foreach(Topic b in blocks) {
        if(LplcModel.IsPin(b)) {
          layouts.Add(BuildPinLayout(b));
          continue;
        }
        List<string> refs;
        referenced.TryGetValue(b.name, out refs);
        layouts.Add(BuildBlockLayout(b, refs));
      }
      foreach(Topic v in variables) {
        if(!LplcModel.IsObserver(v)) layouts.Add(BuildVariableLayout(v));
      }
      PlaceUnpositioned(layouts);
      return layouts;
    }

    private static ElementLayout BuildBlockLayout(Topic block, List<string> referenced) {
      ElementLayout layout = NewLayout(block, true);
      var configuration = LplcModel.Configuration(block);
      List<string> inCatalog = LplcModel.InputCatalog(block);
      List<string> outCatalog = LplcModel.OutputCatalog(block);
      List<string> ins = LplcModel.ShownInputs(block, inCatalog);
      List<string> outs = LplcModel.ShownOutputs(block, outCatalog, referenced);
      JSC.JSValue inputs = block.GetField("LPLC.in");
      int inMaxNameLen = AddPins(layout, ins, inCatalog, true, inputs);
      int outMaxNameLen = AddPins(layout, outs, outCatalog, false, null);
      int inCount = Math.Min(ins.Count, LogramGraphController.MaxPinIndex + 1);
      int outCount = Math.Min(outs.Count, LogramGraphController.MaxPinIndex + 1);
      layout.HeightCells = Math.Max(1, Math.Max(inCount, outCount));
      // LogramGraphController.BuildLayout's estimate verbatim, output-pin column included - the client
      // draws exactly the width/height sent, and the router blocks exactly this footprint.
      int bodyWidthCells = Math.Max(3, (inMaxNameLen * 2 + 2) / 5 + 1 + (outMaxNameLen * 2 + 2) / 5);
      layout.WidthCells = bodyWidthCells + 1;
      if(configuration.Schema.HasMetadata || configuration.Schema.Errors.Count > 0) {
        var resolved = LplcModel.Configurations(block.parent).Resolve(block.name);
        layout.ConfigurationError = string.Join("; ", resolved.Errors.Select(e => e.Key + ": " + e.Value));
        layout.ConstructorRows = new JSL.Array();
        int maxPosition = 0, width = 0, index = 0;
        foreach(var argument in configuration.Schema.Arguments.Where(a => a.Position > 0).OrderBy(a => a.Position)) {
          JSC.JSValue value;
          if(!resolved.Values.TryGetValue(argument.Name, out value)) value = JSC.JSValue.Undefined;
          string formatError, error;
          string text = X13.Plc.ConstructorResolver.Display(argument, value, out formatError);
          var row = JSC.JSObject.CreateObject();
          string label = argument.Label.Length > 256 ? argument.Label.Substring(0, 255) + "…" : argument.Label;
          row["name"] = argument.Name; row["label"] = label;
          row["position"] = Math.Min(255, argument.Position); row["text"] = text;
          row["value"] = value != null && value.Defined ? value : JSC.JSValue.Null;
          row["format"] = argument.Format == null ? JSC.JSValue.Null : (JSC.JSValue)argument.Format;
          row["editable"] = !argument.Readonly;
          if(resolved.Errors.TryGetValue(argument.Name, out error) || resolved.Errors.TryGetValue("", out error) || (error = formatError) != null) row["error"] = error;
          layout.ConstructorRows[index++] = row;
          maxPosition = Math.Max(maxPosition, Math.Min(255, argument.Position));
          width = Math.Max(width, label.Length + text.Length + 4);
        }
        if(index > 0) {
          int offset = maxPosition + 1;
          foreach(var pin in layout.Pins) pin.Index += offset;
          layout.HeightCells = offset + Math.Max(inCount, outCount);
          layout.WidthCells = Math.Max(layout.WidthCells, (width * 6 + 29) / 18);
        }
        return layout;
      }
      layout.ConstructorArgs = LplcModel.ConstructorLabels(block);
      if(layout.ConstructorArgs.Count > 0) {
        int offset = layout.ConstructorArgs.Count + 1;
        int maxLength = block.name.Length + 14;
        foreach(string label in layout.ConstructorArgs) maxLength = Math.Max(maxLength, label.Length);
        foreach(var pin in layout.Pins) pin.Index += offset;
        layout.HeightCells = offset + Math.Max(inCount, outCount);
        layout.WidthCells = Math.Max(layout.WidthCells, (maxLength * 6 + 12 + 17) / 18);
      }
      return layout;
    }

    /// <returns>The longest pin name, for the width estimate.</returns>
    private static int AddPins(ElementLayout layout, List<string> names, List<string> catalog, bool input, JSC.JSValue inputs) {
      int maxNameLen = 0;
      int count = Math.Min(names.Count, LogramGraphController.MaxPinIndex + 1);
      for(int i = 0; i < count; i++) {
        string name = names[i];
        JSC.JSValue value = input ? Property(inputs, name) : null;
        maxNameLen = Math.Max(maxNameLen, name.Length);
        layout.Pins.Add(new PinLayout() {
          Name = name,
          Input = input,
          Index = i,
          Vid = LplcModel.PinVid(layout.Topic, input, name),
          Label = input ? ConstantLabel(value) : null,
          ConstantEditable = input && string.IsNullOrEmpty(value.AsString(null)),
          Error = !catalog.Contains(name),
          Observer = !input && catalog.Contains(name) ? LplcModel.Observer(layout.Topic, name) : null,
        });
      }
      return maxNameLen;
    }

    private static ElementLayout BuildVariableLayout(Topic variable) {
      ElementLayout layout = NewLayout(variable, false);
      SizeAsVariable(layout);
      return layout;
    }

    // Still a block - wires and links name it as one ("Pin.value") - but drawn as a variable.
    private static ElementLayout BuildPinLayout(Topic pin) {
      ElementLayout layout = NewLayout(pin, true);
      layout.IsPin = true;
      layout.Observer = LplcModel.Observer(pin, LplcModel.PinOutput);
      SizeAsVariable(layout);
      return layout;
    }

    private static void SizeAsVariable(ElementLayout layout) {
      int nameLen = layout.Topic.name != null ? layout.Topic.name.Length : 0;
      layout.HeightCells = 1;
      layout.WidthCells = Math.Max(3, (nameLen + 3) * 2 / 5);
    }

    private static ElementLayout NewLayout(Topic t, bool isBlock) {
      return new ElementLayout() {
        Topic = t,
        Vid = LplcModel.Vid(t),
        IsBlock = isBlock,
        Positioned = t.GetField("LPLC.pos").IsObject(),
        X = LogramGraphController.Cell(t.GetField("LPLC.pos.left")),
        Y = LogramGraphController.Cell(t.GetField("LPLC.pos.top")),
      };
    }

    // An element without LPLC.pos - typically a variable created from the device's own menu - would
    // otherwise pile up on cell 0,0. Shown in a column right of everything placed, one under another;
    // nothing is written: the first drag commits a real position.
    private static void PlaceUnpositioned(List<ElementLayout> layouts) {
      int right = 0;
      foreach(ElementLayout l in layouts) {
        if(l.Positioned) right = Math.Max(right, l.X + l.WidthCells + 1);
      }
      int x = right > 0 ? right + 1 : LogramGraphController.MinLeftCell;
      int y = 0;
      foreach(ElementLayout l in layouts) {
        if(l.Positioned) continue;
        l.X = x;
        l.Y = y;
        y += l.HeightCells + 1;
      }
    }

    private static JSC.JSValue Property(JSC.JSValue obj, string name) {
      // By key, not by path: Field would split a key holding a dot.
      return obj.IsObject() ? obj.GetProperty(name) : JSC.JSValue.NotExists;
    }

    /// <summary>A constant link as the text shown next to its pin; null - not a constant.</summary>
    internal static string ConstantLabel(JSC.JSValue link) {
      if(!LplcModel.IsConstant(link)) return null;
      switch(link.ValueType) {
      case JSC.JSValueType.Boolean:
        return (bool)link ? "true" : "false";
      case JSC.JSValueType.Integer:
        return ((int)link).ToString(CultureInfo.InvariantCulture);
      default:
        return ((double)link).ToString("R", CultureInfo.InvariantCulture);
      }
    }

    // === Wires ===

    private void ResolveWires(List<ElementLayout> layouts) {
      Dictionary<string, ElementLayout> blocks = new Dictionary<string, ElementLayout>(StringComparer.Ordinal);
      Dictionary<string, ElementLayout> variables = new Dictionary<string, ElementLayout>(StringComparer.Ordinal);
      WireRoutingPass pass = new WireRoutingPass();
      HashSet<long> blocked = new HashSet<long>();
      foreach(ElementLayout l in layouts) {
        (l.IsBlock ? blocks : variables)[l.Topic.name] = l;
        // Same bounds as LogramGraphController.ResolveWires: the client's canvasSize plus its spare column.
        pass.MaxX = Math.Max(pass.MaxX, l.X + l.WidthCells + 1);
        pass.MaxY = Math.Max(pass.MaxY, l.Y + l.HeightCells);
        WireRoutingPass.Block(blocked, l.X, l.Y, l.WidthCells, l.HeightCells);
      }

      foreach(ElementLayout layout in layouts) {
        if(layout.IsPin) {
          ResolveOneWire(layout.Vid, new LogramWireRouter.GridPoint(layout.X, layout.Y), layout.Topic.GetField(LplcModel.PinInputField), blocks, variables, blocked, pass);
        }
        else if(layout.IsBlock) {
          JSC.JSValue inputs = layout.Topic.GetField("LPLC.in");
          foreach(PinLayout pin in layout.Pins) {
            if(!pin.Input) continue;
            ResolveOneWire(pin.Vid, new LogramWireRouter.GridPoint(layout.X, layout.Y + pin.Index), Property(inputs, pin.Name), blocks, variables, blocked, pass);
          }
        }
        else {
          ResolveOneWire(layout.Vid, new LogramWireRouter.GridPoint(layout.X, layout.Y), layout.Topic.GetField(LplcModel.VarInputField), blocks, variables, blocked, pass);
        }
      }
    }

    // A link names its source by name, as the compiler reads it (LP_DiagramBinder.Connect):
    // "Block.out" - a block's output, "Var" - a variable. Local, and routed, when that source is drawn
    // here; otherwise the vid it would have, non-local - the client greys the pin instead of a wire.
    private void ResolveOneWire(string sinkVid, LogramWireRouter.GridPoint sinkCell, JSC.JSValue link,
      Dictionary<string, ElementLayout> blocks, Dictionary<string, ElementLayout> variables, HashSet<long> blocked, WireRoutingPass pass) {
      string sourceVid = string.Empty;
      LogramWireRouter.GridPoint? sourceCell = null;
      string name, pin;
      if(LplcModel.TrySplitLink(link, out name, out pin)) {
        ElementLayout source;
        if(pin != null) {
          if(blocks.TryGetValue(name, out source)) {
            if(source.IsPin) {
              // A Pin has one output and no pin rows: the wire leaves its own row, as a variable's does.
              sourceVid = source.Vid;
              if(pin == LplcModel.PinOutput) sourceCell = new LogramWireRouter.GridPoint(source.X + source.WidthCells, source.Y);
            }
            else {
              sourceVid = LplcModel.PinVid(source.Topic, false, pin);
              PinLayout sourcePin = source.Pins.Find(p => !p.Input && p.Name == pin);
              if(sourcePin != null) sourceCell = new LogramWireRouter.GridPoint(source.X + source.WidthCells, source.Y + sourcePin.Index);
            }
          }
          else {
            sourceVid = LplcModel.Vid(_program) + "/" + name + "/" + LplcModel.OutPrefix + pin;
          }
        }
        else if(variables.TryGetValue(name, out source)) {
          sourceVid = source.Vid;
          sourceCell = new LogramWireRouter.GridPoint(source.X + source.WidthCells, source.Y);
        }
        else if(_device != null) {
          sourceVid = LplcModel.Vid(_device) + "/" + name;
        }
      }

      List<LogramWireRouter.GridPoint> cells = null;
      if(sourceCell.HasValue) {
        string owner = sourceVid;
        cells = LogramWireRouter.FindPath(sinkCell, sourceCell.Value,
          (x, y) => pass.IsBlocked(x, y, blocked),
          (x1, y1, x2, y2) => pass.StepCost(x1, y1, x2, y2, owner),
          pass.MinStepCost(owner));
        pass.Claim(cells, owner);
      }
      SendWire(sinkVid, sourceVid, cells);
    }

    private void SendWire(string sinkVid, string sourceVid, List<LogramWireRouter.GridPoint> cells) {
      bool local = cells != null;
      StringBuilder fp = new StringBuilder();
      fp.Append(sourceVid).Append('|').Append(local);
      if(cells != null) {
        foreach(LogramWireRouter.GridPoint p in cells) fp.Append('|').Append(p.X).Append(',').Append(p.Y);
      }
      string fingerprint = fp.ToString();
      string previous;
      if(_wireFingerprint.TryGetValue(sinkVid, out previous) && previous == fingerprint) return;
      _wireFingerprint[sinkVid] = fingerprint;

      JSC.JSObject dto = ViewProtocolSerializer.RowBase(ViewMessageTypes.EvntUpd, sinkVid);
      dto["sourceVid"] = sourceVid;
      dto["sourceLocal"] = local;
      if(cells != null) {
        JSL.Array pts = new JSL.Array();
        int i = 0;
        foreach(LogramWireRouter.GridPoint p in cells) {
          JSC.JSObject pt = JSC.JSObject.CreateObject();
          pt["x"] = new JSL.Number(p.X);
          pt["y"] = new JSL.Number(p.Y);
          pts[i++] = pt;
        }
        dto["sourcePath"] = pts;
      }
      _send(dto);
    }

    // === Serialization - the rows LogramGraphController sends, see its Serialize*Row ===

    private JSC.JSObject SerializeCanvasRow() {
      JSC.JSObject dto = ViewProtocolSerializer.RowBase(ViewMessageTypes.EvntAdd, RootVid);
      dto["level"] = new JSL.Number(0);
      dto["icon"] = IconResource.Resolve(_program, null) ?? string.Empty;
      dto["name"] = _program.name ?? string.Empty;
      dto["editor"] = LogramGraphController.EditorCanvas;
      JSC.JSValue state = _program.GetState();
      dto["value"] = state.Defined ? state : JSC.JSValue.Null;
      JSC.JSValue modified = _program.GetField("LPLC.modified");
      dto["plcModified"] = modified.ValueType == JSC.JSValueType.Boolean ? modified : JSC.JSValue.Null;
      return dto;
    }

    private JSC.JSObject SerializeElementRow(ElementLayout layout) {
      JSC.JSObject dto = ViewProtocolSerializer.RowBase(ViewMessageTypes.EvntAdd, layout.Vid);
      dto["level"] = new JSL.Number(1);
      dto["icon"] = IconOf(layout);
      dto["name"] = layout.Topic.name ?? string.Empty;
      dto["editor"] = EditorOf(layout);
      dto["x"] = new JSL.Number(layout.X);
      dto["y"] = new JSL.Number(layout.Y);
      dto["width"] = new JSL.Number(layout.WidthCells);
      dto["height"] = new JSL.Number(layout.HeightCells);
      if(layout.ConstructorArgs.Count > 0) dto["constructorArgs"] = ConstructorArray(layout);
      if(layout.ConstructorRows != null) dto["constructorRows"] = layout.ConstructorRows;
      if(!string.IsNullOrEmpty(layout.ConfigurationError)) dto["configurationError"] = layout.ConfigurationError;
      if(layout.IsPin) WriteTrace(dto, layout.Vid, layout.Observer);
      return dto;
    }

    private static JSL.Array ConstructorArray(ElementLayout layout) {
      var values = new JSL.Array();
      for(int i = 0; i < layout.ConstructorArgs.Count; i++) values[i] = layout.ConstructorArgs[i];
      return values;
    }

    private JSC.JSObject SerializePinRow(PinLayout pin) {
      JSC.JSObject dto = ViewProtocolSerializer.RowBase(ViewMessageTypes.EvntAdd, pin.Vid);
      dto["level"] = new JSL.Number(2);
      dto["name"] = pin.Name;
      dto["editor"] = LogramGraphController.EditorPin;
      dto["pinDirection"] = pin.Input ? "in" : "out";
      dto["pinIndex"] = new JSL.Number(pin.Index);
      dto["pinError"] = pin.Error;
      dto["constantEditable"] = pin.ConstantEditable;
      if(pin.ConstantEditable) {
        dto["displayValue"] = pin.Label ?? string.Empty;
      }
      if(!pin.Input) WriteTrace(dto, pin.Vid, pin.Observer);
      return dto;
    }

    private sealed class ElementLayout {
      internal Topic Topic;
      internal string Vid;
      internal bool IsBlock;
      /// <summary>An LPLC/Pin - a block drawn as a variable.</summary>
      internal bool IsPin;
      internal Topic Observer;
      internal bool Positioned;
      internal int X;
      internal int Y;
      internal int WidthCells;
      internal int HeightCells;
      internal JSL.Array ConstructorRows;
      internal string ConfigurationError;
      internal List<PinLayout> Pins = new List<PinLayout>();
      internal List<string> ConstructorArgs = new List<string>();
    }

    private sealed class PinLayout {
      internal string Name;
      internal bool Input;
      internal int Index;
      internal string Vid;
      internal string Label;
      internal bool ConstantEditable;
      internal bool Error;
      internal Topic Observer;
    }
  }
}
