///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using NiL.JS.Core;

namespace X13.Repository {
  /// <summary>The order changes are applied and published in, one list per phase.</summary>
  /// <remarks>It used to be <c>((int)Art) &gt;&gt; 2</c> - the numeric values of a public enum
  /// silently carried the schedule of the tick, so renumbering them would have reordered it.
  /// Structure first, because a topic has to exist before anything can be said about it; removals
  /// late, because a change to a topic that the same tick also removes still belongs to the tick
  /// that had it; acknowledgements last, because they mean "everything above is delivered".</remarks>
  internal enum Phase {
    Struct = 0,   // create, move
    Sub = 1,      // the snapshot a new subscription is owed
    Field = 2,    // manifest
    State = 3,
    Remove = 4,
    Ack = 5,
  }

  /// <summary>Something asked of the repository. Never leaves the assembly.</summary>
  /// <remarks>A command and the event it produces used to be one object, with Art rewritten in
  /// place from setState to changedState as it travelled. Publication then had to skip whatever
  /// was still a command, and plugins carried filters against kinds that could not reach them:
  /// LiteDB_Pl tested for setState, setField and unsubscribe, and not one of the three was ever
  /// published. Splitting the two makes such a filter unwritable rather than merely useless.</remarks>
  internal abstract class Cmd {
    public readonly Topic Target;
    public readonly Topic Author;

    protected Cmd(Topic target, Topic author) {
      this.Target = target;
      this.Author = author;
    }
    public abstract Phase Phase { get; }

    /// <summary>Carries the change out and says what happened. Null when nothing did.</summary>
    /// <remarks>Runs on the tick thread, in phase order, and only once every command of the batch
    /// is off the queue - which is what puts a write to a topic in front of that topic's removal
    /// when the same tick carries both.</remarks>
    public abstract TopicEvent Apply();
  }

  internal sealed class CmdCreate : Cmd {
    public CmdCreate(Topic target, Topic author) : base(target, author) { }
    public override Phase Phase { get { return Phase.Struct; } }
    public override TopicEvent Apply() { return TopicEvent.Created(Target, Author); }
  }

  internal sealed class CmdMove : Cmd {
    public readonly string OldPath;
    public CmdMove(Topic target, string oldPath, Topic author) : base(target, author) { this.OldPath = oldPath; }
    public override Phase Phase { get { return Phase.Struct; } }
    public override TopicEvent Apply() { return TopicEvent.Moved(Target, OldPath, Author); }
  }

  internal sealed class CmdRemove : Cmd {
    public CmdRemove(Topic target, Topic author) : base(target, author) { }
    public override Phase Phase { get { return Phase.Remove; } }
    public override TopicEvent Apply() {
      JSValue old = Target.GetState();
      Topic.SetValue(Target, null);   // a removed topic must not go on answering with its last value
      Topic.Unlink(Target);
      return TopicEvent.Removed(Target, old, Author);
    }
  }

  internal sealed class CmdState : Cmd {
    public readonly JSValue Value;
    public CmdState(Topic target, JSValue value, Topic author) : base(target, author) { this.Value = value; }
    public override Phase Phase { get { return Phase.State; } }
    public override TopicEvent Apply() {
      JSValue old = Target.GetState();
      if (object.ReferenceEquals(old, Value)) {
        return null;   // written, but not changed: nothing happened and nobody is told
      }
      Topic.SetValue(Target, Value);
      return TopicEvent.StateChanged(Target, old, Author);
    }
  }

  /// <summary>One write into the manifest. Several in a tick fold into the first one's command.</summary>
  /// <remarks>Only the first survives the drain - Topic.SetField merges the rest into the
  /// manifest it is building and answers false - so the event names the field written first and
  /// carries the manifest as it stood before the whole batch.</remarks>
  internal sealed class CmdField : Cmd {
    public readonly string Path;
    public readonly JSValue Value;
    public CmdField(Topic target, string path, JSValue value, Topic author) : base(target, author) {
      this.Path = path;
      this.Value = value;
    }
    public override Phase Phase { get { return Phase.Field; } }
    public override TopicEvent Apply() { return Topic.SetField2(Target); }
  }

  /// <summary>A new registration asking for the state that is already there.</summary>
  /// <remarks>Applied nowhere: the snapshot is spelled out during the drain, because it is one
  /// event per topic in scope and those belong to the subscription phase, ahead of whatever else
  /// the same tick is about to change.</remarks>
  internal sealed class CmdSubscribe : Cmd {
    public readonly SubRec Sub;
    public CmdSubscribe(Topic target, SubRec sub) : base(target, target) { this.Sub = sub; }
    public override Phase Phase { get { return Phase.Sub; } }
    public override TopicEvent Apply() { return null; }
  }

  /// <summary>Says the subscription is in place, for a registration that already was.</summary>
  internal sealed class CmdAck : Cmd {
    public readonly SubRec Sub;
    public CmdAck(Topic target, SubRec sub) : base(target, target) { this.Sub = sub; }
    public override Phase Phase { get { return Phase.Ack; } }
    public override TopicEvent Apply() { return TopicEvent.Ready(Target, Sub); }
  }
}
