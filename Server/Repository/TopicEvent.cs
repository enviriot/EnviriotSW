///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using NiL.JS.Core;

namespace X13.Repository {
  /// <summary>Событие, произошедшее с топиком. Каждый вид описывает уже произошедшее изменение.</summary>
  public enum EventKind {
    Created,
    Moved,
    StateChanged,
    FieldChanged,
    Removed,
    /// <summary>Состояние, уже хранящееся в топике, передаваемое только что созданной подписке.</summary>
    Snapshot,
    /// <summary>Передача снимка завершена; далее поступают только изменения.</summary>
    Ready,
  }

  /// <summary>Одно опубликованное изменение: доступно только для чтения и типизировано видом события.</summary>
  public sealed class TopicEvent {
    /// <summary>Топик, с которым произошло событие.</summary>
    public readonly Topic Source;
    public readonly EventKind Kind;
    /// <summary>Источник изменения, если он указан; используется для предотвращения обратного распространения изменения.</summary>
    public readonly Topic Author;
    /// <summary>Moved: прежний путь топика. Для всех остальных видов равен null.</summary>
    public readonly string OldPath;
    /// <summary>FieldChanged: путь изменённого поля. Для всех остальных видов равен null.</summary>
    public readonly string FieldPath;

    internal readonly JSValue OldState;      // StateChanged, Removed: предыдущее состояние
    internal readonly JSValue OldManifest;   // FieldChanged: предыдущий манифест
    internal readonly SubRec Sub;            // Snapshot, Ready: целевая подписка

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

    internal static TopicEvent Created(Topic t, Topic author) {
      return new TopicEvent(t, EventKind.Created, author, null, null, null, null, null);
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
