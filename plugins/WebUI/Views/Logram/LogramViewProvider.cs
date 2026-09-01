///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using JSC = NiL.JS.Core;
using JSL = NiL.JS.BaseLibrary;
using System;
using System.Collections.Generic;
using X13.Repository;
using X13.WebUI.Helpers;

namespace X13.WebUI {
  // vid-routed entry point for Logram diagrams (view name "logram") - one
  // LogramGraphController per open document, keyed by the diagram's own Topic.
  //
  // Keyed by Topic, not by the vid the client opened with: a vid is a path, and Topic.Move
  // rewrites Topic._path in place (Topic.I.UpdatePath), so every vid naming a renamed diagram
  // goes stale while the Topic reference stays valid. The reference needs no id of its own -
  // it is already stable, costs no lookup and cannot be desynchronised. Same idiom as
  // LogramPl._items and PersistentStorage's Dictionary<Topic, ...>. vid remains what it was
  // designed to be: the identifier on the wire.
  internal sealed class LogramViewProvider : ViewProviderBase {
    private const string ViewName = "logram";
    private readonly Action<JSC.JSObject> _send;
    // Null until the session hands one in - controllers read null as "inline".
    private readonly Action<string, Action> _post;
    private readonly Dictionary<Topic, LogramGraphController> _open = new Dictionary<Topic, LogramGraphController>();
    // No lock: Open/Close/Dispose arrive through the session's queue, and so does ForgetRoot (a
    // controller noticing its own root was removed, now reached from a queued callback). Both
    // therefore run on the engine thread. The lock that used to sit here existed only because
    // those two arrived on different threads.

    internal LogramViewProvider(Action<JSC.JSObject> send, Action<string, Action> post = null, Func<Topic> prim = null) {
      _send = send;
      _post = post;
      _prim = prim;
    }

    private readonly Func<Topic> _prim;

    public override bool CanHandle(string vid) {
      return VidHelper.GetView(vid) == ViewName;
    }

    public override ViewOpResult Open(string vid, string view) {
      if(string.IsNullOrEmpty(vid) || VidHelper.GetView(vid) != ViewName) {
        return ViewOpResult.Error("view_target_not_found", "View target not found: " + (vid ?? "<null>"));
      }
      if(!string.Equals(view, ViewName, StringComparison.Ordinal)) {
        return ViewOpResult.Error("view_open_not_supported", "Open is not supported for view: " + (view ?? "<null>"));
      }

      string topicPath = VidHelper.GetTopicPath(vid);
      Topic topic = Topic.root.Get(topicPath, false);
      if(topic == null) {
        return ViewOpResult.Error("topic_not_found", "Topic not found: " + topicPath);
      }
      if(!IsDiagram(topic)) {
        return ViewOpResult.Error("logram_type_required", "Topic is not a Logram diagram: " + topicPath);
      }

      LogramGraphController existing;
      _open.TryGetValue(topic, out existing);
      _open.Remove(topic);
      if(existing != null) existing.Dispose();

      LogramGraphController controller = new LogramGraphController(_send, topic, ViewName, ForgetRoot, _post);
      _open[topic] = controller;
      controller.Open();

      return ViewOpResult.Open(ViewName, controller.RootVid, topic.name);
    }

    // Drag-to-move commits the element's own vid (logram#<path>) with {top, left}.
    // Merges each key via a dotted "Logram.<key>" path (Topic.SetField's existing
    // dotted-path merge-write) rather than replacing the whole "Logram" object -
    // otherwise this would silently wipe out any other sibling field under it (e.g.
    // a future Logram.trace) every time a block is dragged.
    public override ViewOpResult Commit(string vid, JSC.JSValue value) {
      string topicPath = VidHelper.GetTopicPath(vid);
      Topic topic = Topic.root.Get(topicPath, false);
      if(topic == null || topic.parent == null) {
        return ViewOpResult.Error("topic_not_found", "Topic not found: " + topicPath);
      }
      if(!IsDiagramMember(topic)) {
        return ViewOpResult.Error("view_target_not_found", "Not part of a Logram diagram: " + topicPath);
      }
      if(!value.IsObject()) {
        return ViewOpResult.Error("commit_value_invalid", "Expected an object with top/left");
      }
      Topic prim = _prim == null ? null : _prim();
      foreach(var entry in value) topic.SetField("Logram." + entry.Key, entry.Value, prim);
      return ViewOpResult.Success();
    }

    private static bool IsDiagram(Topic topic) {
      return topic != null && string.Equals(topic.GetField("type").AsString(null), "Core/Logram", StringComparison.Ordinal);
    }

    // A logram# vid resolves against Topic.root like any other, so without this every write
    // below reached ANY topic in the tree - and bind/unbind are not cosmetic: cctor.LoBind is
    // what LoVariable.ManifestChanged reads, so they rewire or sever running logic outside the
    // document the user has open. Part 9 settled the same question for the Inspector children
    // tree with TopicTreeController.IsWithinRoot; this is that guard for Logram. Two levels is
    // the same boundary LogramGraphController.ResolveOneWire already calls "local" - the
    // diagram itself, one of its elements, or one of that element's pins.
    private static bool IsDiagramMember(Topic topic) {
      if(topic == null) return false;
      return IsDiagram(topic)
        || IsDiagram(topic.parent)
        || (topic.parent != null && IsDiagram(topic.parent.parent));
    }

    // "bind"/"unbind" set or clear a wire (cctor.LoBind is the sink's own field, see
    // LogramGraphController.ResolveOneWire) - Logram-specific, handled directly. Drop-
    // target compatibility (opposite polarity) is checked client-side before this is
    // even called (see logram-document.js #tryConnect); the guard here is just
    // against nonsense (missing/unresolvable source, self-bind), not a full redo of
    // that check - a source that resolves but sits outside this diagram's subtree, or
    // has the wrong polarity, still round-trips fine as an "external"/unrouted wire
    // (see ResolveOneWire's local/external split), it just won't draw a line.
    // Everything else (delete, and anything TopicRpcDispatcher already knows)
    // reuses the same generic dispatcher Workspace/Inspector already commit through,
    // including its Required-attribute delete guard - no need to duplicate that here.
    public override ViewOpResult ExecuteRpc(string vid, string cmd, JSC.JSValue args) {
      string topicPath = VidHelper.GetTopicPath(vid);
      Topic topic = Topic.root.Get(topicPath, false);
      if(topic == null) {
        return ViewOpResult.Error("topic_not_found", "Topic not found: " + topicPath);
      }
      // Covers the TopicRpcDispatcher fallback at the end of this method too, delete included.
      if(!IsDiagramMember(topic)) {
        return ViewOpResult.Error("view_target_not_found", "Not part of a Logram diagram: " + topicPath);
      }
      if(string.Equals(cmd, "unbind", StringComparison.Ordinal)) {
        topic.SetField("cctor.LoBind", JSC.JSValue.Null, _prim == null ? null : _prim());
        return ViewOpResult.Success();
      }
      // Pin context menu's checkable "Trace" (BuildPinMenu) - ports
      // miTrace_Click's plain flip of the same field (ES/Logram/LogramView.cs).
      if(string.Equals(cmd, "trace", StringComparison.Ordinal)) {
        bool traced = topic.GetField("Logram.trace").AsBool(false);
        topic.SetField("Logram.trace", !traced);
        return ViewOpResult.Success();
      }
      if(string.Equals(cmd, "bind", StringComparison.Ordinal)) {
        string sourceVid = args.Field("source").AsString(null);
        string sourcePath = VidHelper.GetTopicPath(sourceVid);
        if(string.IsNullOrEmpty(sourcePath)) {
          return ViewOpResult.Error("bind_source_missing", "Missing or invalid source vid");
        }
        Topic sourceTopic = Topic.root.Get(sourcePath, false);
        if(sourceTopic == null) {
          return ViewOpResult.Error("bind_source_not_found", "Source topic not found: " + sourcePath);
        }
        if(sourceTopic == topic) {
          return ViewOpResult.Error("bind_source_invalid", "Cannot bind a pin to itself");
        }
        topic.SetField("cctor.LoBind", sourcePath);
        return ViewOpResult.Success();
      }
      if(cmd != null && cmd.StartsWith(LogramPaletteBuilder.AddBlockCmdPrefix, StringComparison.Ordinal)) {
        return ExecuteAddBlock(topic, cmd.Substring(LogramPaletteBuilder.AddBlockCmdPrefix.Length), args);
      }
      if(string.Equals(cmd, "add-variable", StringComparison.Ordinal)) {
        return ExecuteAddVariable(topic, args);
      }
      return TopicRpcDispatcher.Execute(topic, cmd, args);
    }

    // Mirrors ES's drop-to-create (ES/Logram/LogramView.cs LogramView_Drop): resolve
    // the registry descriptor, auto-name (prefix + "01", "02", ... - first free),
    // stamp the click's grid cell into the manifest's Logram.top/left before
    // creating (same JsLib.SetField dotted-merge ES uses), then create the block
    // itself and every Required-attribute pin its descriptor declares - the recursive
    // half nothing else in Server/WebUI does (see Server/EsBroker/EsConnection.cs
    // Create, the only other place this walk exists, unreachable from here since
    // it's EsBroker-specific). Without it LogramGraphController.BuildLayout finds no
    // pin child topics and the block renders with no pins at all.
    private static ViewOpResult ExecuteAddBlock(Topic diagram, string typePath, JSC.JSValue args) {
      Topic descriptor = TypeHelper.ResolveTypeTopic(typePath);
      JSC.JSValue descState = descriptor?.GetState();
      if(descriptor == null || !descState.IsObject()) {
        return ViewOpResult.Error("logram_type_not_found", "Block type not found: " + (typePath ?? "<null>"));
      }

      string prefix = descState.AsString("namePrefix", "U");
      string name = NextName(diagram, prefix);

      int top = Math.Max(0, args.AsInt("top", 0));
      int left = Math.Max(LogramGraphController.MinLeftCell, args.AsInt("left", 0));

      JSC.JSValue manifestSrc = descState["manifest"];
      JSC.JSValue manifest = manifestSrc.IsObject() ? JsLib.Clone(manifestSrc) : JSC.JSObject.CreateObject();
      manifest = JsLib.SetField(manifest, "Logram.top", new JSL.Number(top));
      manifest = JsLib.SetField(manifest, "Logram.left", new JSL.Number(left));

      JSC.JSValue state = descState["default"];
      Topic block = Topic.I.Get(diagram, name, true, null, false, false);
      Topic.I.Fill(block, state != null && state.Defined ? JsLib.Clone(state) : JSC.JSValue.Null, manifest, null);

      CreateRequiredChildren(block, descState["Children"]);
      return ViewOpResult.Success();
    }

    // Mirrors ES's drag-topic-to-canvas (ES/Logram/LogramView.cs LogramView_Drop, the
    // DTopic/non-Ext-LBDescr branch, see logram-document.js's #onSurfaceDrop for the
    // client half): creates a child topic under the diagram whose State is a COPY of
    // the dragged topic's current value (not a live reference) and whose
    // cctor.LoBind points back at the dragged topic - LogramGraphController renders
    // that exactly like any other variable (ChildrenSchema returns null for it, same
    // "no pin schema" rule). Auto-names like ES does: the source's own short name,
    // else "<sourceParent>_<sourceName>", else "<sourceName>_<i>" first free - and,
    // same as ES, back-fills the DRAGGED topic's own cctor.LoBind to point at the new
    // variable when it doesn't already have one (so a bare sensor topic with no
    // existing binding gets auto-wired both ways from a single drop).
    private static ViewOpResult ExecuteAddVariable(Topic diagram, JSC.JSValue args) {
      string sourceVid = args.Field("source").AsString(null);
      string sourcePath = VidHelper.GetTopicPath(sourceVid);
      if(string.IsNullOrEmpty(sourcePath)) {
        return ViewOpResult.Error("add_variable_source_missing", "Missing or invalid source vid");
      }
      Topic source = Topic.root.Get(sourcePath, false);
      if(source == null) {
        return ViewOpResult.Error("add_variable_source_not_found", "Source topic not found: " + sourcePath);
      }
      if(source == diagram) {
        return ViewOpResult.Error("add_variable_source_invalid", "Cannot bind a variable to its own diagram");
      }

      string name = NextVariableName(diagram, source);

      // No MinLeftCell floor here, unlike ExecuteAddBlock: variables are exempt from it and
      // may sit flush against the left edge (see the constant).
      int top = Math.Max(0, args.AsInt("top", 0));
      int left = Math.Max(0, args.AsInt("left", 0));

      JSC.JSValue manifest = JSC.JSObject.CreateObject();
      manifest = JsLib.SetField(manifest, "Logram.top", new JSL.Number(top));
      manifest = JsLib.SetField(manifest, "Logram.left", new JSL.Number(left));
      manifest = JsLib.SetField(manifest, "cctor.LoBind", new JSL.String(source.path));

      JSC.JSValue state = source.GetState();
      Topic variable = Topic.I.Get(diagram, name, true, null, false, false);
      Topic.I.Fill(variable, state != null && state.Defined ? JsLib.Clone(state) : JSC.JSValue.Null, manifest, null);

      if(string.IsNullOrEmpty(source.GetField("cctor.LoBind").AsString(null))) {
        source.SetField("cctor.LoBind", variable.path);
      }
      return ViewOpResult.Success();
    }

    private static string NextVariableName(Topic diagram, Topic source) {
      string name = source.name;
      if(diagram.Get(name, false) != null) {
        name = source.parent != null ? source.parent.name + "_" + source.name : null;
        if(name == null || diagram.Get(name, false) != null) {
          int i = 1;
          string candidate;
          do {
            candidate = source.name + "_" + i;
            i++;
          } while(diagram.Get(candidate, false) != null);
          name = candidate;
        }
      }
      return name;
    }

    private static string NextName(Topic parent, string prefix) {
      int i = 1;
      string name;
      do {
        name = prefix + i.ToString("D2");
        i++;
      } while(parent.Get(name, false) != null);
      return name;
    }

    private static void CreateRequiredChildren(Topic parent, JSC.JSValue childrenSchema) {
      if(!childrenSchema.IsObject()) return;
      foreach(var entry in childrenSchema) {
        int attr = entry.Value.AsInt("manifest.attr", 0);
        if((attr & (int)Topic.Attribute.Required) == 0) continue;
        JSC.JSValue childState = entry.Value.Field("default");
        JSC.JSValue childManifest = entry.Value.Field("manifest");
        Topic child = Topic.I.Get(parent, entry.Key, true, null, false, false);
        Topic.I.Fill(
          child,
          childState != null && childState.Defined ? JsLib.Clone(childState) : JSC.JSValue.Null,
          childManifest != null && childManifest.Defined ? JsLib.Clone(childManifest) : null,
          null);
      }
    }

    // Right-click asks for this, on the canvas background (vid is the diagram's own
    // root, e.g. "logram#/Test/L1"), an existing block/variable (vid is that
    // element's own vid, e.g. "logram#/Test/L1/A01") or one of a block's pins (vid
    // is the pin's own vid, e.g. "logram#/Test/L1/A01/Q") - see logram-document.js's
    // #onCanvasContextMenu/#onElementContextMenu/#onPinContextMenu. The root gets the
    // "Add block" registry menu (cmd:"add-block:<Timer/Oscillator>"); a pin gets
    // Open/Show in Workspace/Trace/Delete (LogramPaletteBuilder.BuildPinMenu); any
    // other element gets Delete + "Add pin" for whichever declared pins aren't
    // already there.
    public override ViewOpResult BuildMenu(string vid, out List<MenuItemDto> items) {
      items = null;
      string topicPath = VidHelper.GetTopicPath(vid);
      Topic topic = Topic.root.Get(topicPath, false);
      if(topic == null) {
        return ViewOpResult.Error("topic_not_found", "Topic not found: " + topicPath);
      }
      bool isDiagramRoot = string.Equals(topic.GetField("type").AsString(null), "Core/Logram", StringComparison.Ordinal);
      items = isDiagramRoot ? LogramPaletteBuilder.BuildAddMenu()
        : LogramPaletteBuilder.IsPin(topic) ? LogramPaletteBuilder.BuildPinMenu(topic)
        : LogramPaletteBuilder.BuildElementMenu(topic);
      return ViewOpResult.Success();
    }

    // The controller detected that its own diagram topic was removed and has already torn
    // itself down - drop the entry so a later req.* for that vid gets a clean
    // view_target_not_found instead of reaching a disposed controller, and so the controller
    // and its caches are not held for the life of the session.
    //
    // Looked up by the controller's own root Topic, then removed only if the stored entry IS
    // this controller. The key half is now exact (a Topic reference cannot go stale the way the
    // opened-with vid did), so the scan this used to need is gone; the identity check stays,
    // because it answers a different question - without it a late callback from a torn-down
    // controller would evict the live replacement a concurrent Open stored for the same diagram.
    private void ForgetRoot(LogramGraphController controller) {
      if(controller == null) return;
      LogramGraphController current;
      if(_open.TryGetValue(controller.Root, out current) && current == controller) {
        _open.Remove(controller.Root);
      }
    }

    // Resolves the vid to its Topic and closes by that, so a diagram renamed while open still
    // closes: the client closes with the vid it was last given (the canvas row is re-stated
    // under the new path), and that resolves to the same Topic the entry is keyed by.
    public override ViewOpResult Close(string vid) {
      string topicPath = VidHelper.GetTopicPath(vid);
      LogramGraphController controller = null;
      Topic topic = string.IsNullOrEmpty(topicPath) ? null : Topic.root.Get(topicPath, false);
      if(topic != null) {
        _open.TryGetValue(topic, out controller);
        _open.Remove(topic);
      }
      if(controller != null) controller.Dispose();
      return ViewOpResult.Success();
    }

    public override void Dispose() {
      List<LogramGraphController> doomed = new List<LogramGraphController>(_open.Values);
      _open.Clear();
      foreach(LogramGraphController controller in doomed) controller.Dispose();
    }
  }
}
