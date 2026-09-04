///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using NiL.JS.Core;

namespace X13.Repository {
  /// <summary>What happened to a topic. Every kind here is something that already has.</summary>
  /// <remarks>The enum a subscriber used to receive also carried setState, setField and
  /// unsubscribe - requests, not outcomes, which publication filtered out and which plugins
  /// nevertheless tested for. They are gone, and those tests with them.</remarks>
  public enum EventKind {
    Created,
    Moved,
    StateChanged,
    FieldChanged,
    Removed,
    /// <summary>The state a topic already held, handed to a subscription that just began.</summary>
    Snapshot,
    /// <summary>The snapshot is complete; from here on only changes arrive.</summary>
    Ready,
  }

  /// <summary>One published change: read-only, and typed by kind rather than by convention.</summary>
  /// <remarks>What this replaces was a single untyped payload whose meaning depended on the kind -
  /// <c>object o</c> holding a path, a value, a field name or a SubRec by turns, and
  /// <c>old_o</c> holding either the previous state or the previous manifest. Reading the wrong
  /// one answered null rather than failing, so the mistake surfaced as a missing update somewhere
  /// else entirely.</remarks>
  public sealed class TopicEvent {
    /// <summary>The topic this happened to.</summary>
    public readonly Topic Source;
    public readonly EventKind Kind;
    /// <summary>Who caused it, where they said so - what keeps a change from echoing back.</summary>
    public readonly Topic Author;
    /// <summary>Moved: the path the topic had before. Null for every other kind.</summary>
    public readonly string OldPath;
    /// <summary>FieldChanged: the field written first in this batch. Null for every other kind.</summary>
    public readonly string FieldPath;

    internal readonly JSValue OldState;      // StateChanged, Removed
    /// <summary>The manifest this event was computed against.</summary>
    /// <remarks>FieldChanged: what the batch displaced. Created: what the topic was declared with,
    /// which is NOT its manifest by the time anyone is told - Created is applied in the Struct
    /// phase and published after the Field phase has run, so a field written in the same tick is
    /// already in place. Reading the live manifest from a Created event therefore reports what a
    /// FieldChanged in that same tick is about to report again.</remarks>
    internal readonly JSValue OldManifest;   // FieldChanged, Created
    internal readonly SubRec Sub;            // Snapshot, Ready

    private TopicEvent(Topic source, EventKind kind, Topic author, string oldPath, string fieldPath, JSValue oldState, JSValue oldManifest, SubRec sub) {
      this.Source = source;
      this.Kind = kind;
      this.Author = author;
      this.OldPath = oldPath;
      this.FieldPath = fieldPath;
      this.OldState = oldState;
      this.OldManifest = oldManifest;
      this.Sub = sub;
    }

    internal static TopicEvent Created(Topic t, JSValue manifest, Topic author) {
      return new TopicEvent(t, EventKind.Created, author, null, null, null, manifest, null);
    }
    internal static TopicEvent Moved(Topic t, string oldPath, Topic author) {
      return new TopicEvent(t, EventKind.Moved, author, oldPath, null, null, null, null);
    }
    internal static TopicEvent StateChanged(Topic t, JSValue oldState, Topic author) {
      return new TopicEvent(t, EventKind.StateChanged, author, null, null, oldState, null, null);
    }
    internal static TopicEvent FieldChanged(Topic t, string fieldPath, JSValue oldManifest, Topic author) {
      return new TopicEvent(t, EventKind.FieldChanged, author, null, fieldPath, null, oldManifest, null);
    }
    internal static TopicEvent Removed(Topic t, JSValue oldState, Topic author) {
      return new TopicEvent(t, EventKind.Removed, author, null, null, oldState, null, null);
    }
    internal static TopicEvent Snapshot(Topic t, SubRec sub) {
      return new TopicEvent(t, EventKind.Snapshot, t, null, null, null, null, sub);
    }
    internal static TopicEvent Ready(Topic t, SubRec sub) {
      return new TopicEvent(t, EventKind.Ready, t, null, null, null, null, sub);
    }

    public override string ToString() {
      return string.Concat(Source.path, "[", Kind.ToString(), "]", FieldPath == null ? string.Empty : "." + FieldPath);
    }
  }
}
