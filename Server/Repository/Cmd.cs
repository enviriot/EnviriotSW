///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using NiL.JS.Core;

namespace X13.Repository {
  /// <summary>Порядок применения и публикации изменений: отдельный список для каждой фазы.</summary>
  /// <remarks>Сначала обрабатывается структура, поскольку топик должен существовать до публикации сведений о нём;
  /// удаление выполняется ближе к концу, поскольку изменение топика, удаляемого в том же тике, ещё относится
  /// к тику, в котором он существовал; подтверждения идут последними, поскольку означают: «всё выше доставлено».</remarks>
  internal enum Phase {
    Struct = 0,   // создание, перемещение
    Sub = 1,      // снимок, который должна получить новая подписка
    Field = 2,    // манифест
    State = 3,
    Remove = 4,
    Ack = 5,
  }

  /// <summary>Запрос к репозиторию.</summary>
  internal abstract class Cmd {
    public readonly Topic Target;
    public readonly Topic Author;

    protected Cmd(Topic target, Topic author) {
      this.Target = target;
      this.Author = author;
    }
    public abstract Phase Phase { get; }

    /// <summary>Выполняет изменение и сообщает о результате. Возвращает null, если ничего не произошло.</summary>
    /// <remarks>Выполняется в потоке тика, в порядке фаз и только после извлечения всех команд пакета
    /// из очереди. Поэтому запись в топик выполняется перед его удалением, если обе команды попали
    /// в один тик.</remarks>
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
      Topic.SetValue(Target, null);   // удалённый топик не должен продолжать возвращать последнее значение
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
      if (JsLib.SameValue(old, Value)) {
        return null;   // значение записано, но не изменилось: событие не создаётся и никому не отправляется
      }
      Topic.SetValue(Target, Value);
      return TopicEvent.StateChanged(Target, old, Author);
    }
  }

  /// <summary>Одна запись в манифест и одно событие.</summary>
  /// <remarks>Несколько записей в один топик в течение тика используют общий пакет: вместе они формируют
  /// один новый манифест, который устанавливает первая применённая команда. При этом каждая команда сообщает
  /// собственный путь, поскольку потребитель, сопоставляющий FieldPath с известным именем, должен увидеть
  /// именно свою запись, а не ту, которая случайно была первой.</remarks>
  internal sealed class CmdField : Cmd {
    public readonly string Path;
    public readonly JSValue Value;
    /// <summary>Общий для всех записей в этот топик в текущем тике; присоединяется при постановке команды в очередь.</summary>
    public Topic.FieldBatch Batch;

    public CmdField(Topic target, string path, JSValue value, Topic author) : base(target, author) {
      this.Path = path;
      this.Value = value;
    }
    public override Phase Phase { get { return Phase.Field; } }
    public override TopicEvent Apply() {
      Topic.SetField2(Target, Batch);
      // Сравнение выполняется после замены, когда доступны обе части: oldManifest содержит вытесненный
      // пакетом манифест, а топик уже содержит новый.
      if (JsLib.SameValue(JsLib.Field(Batch.oldManifest, Path), Target.GetField(Path))) {
        return null;
      }
      return TopicEvent.FieldChanged(Target, Path, Batch.oldManifest, Author);
    }
  }

  /// <summary>Новая регистрация, запрашивающая уже существующее состояние.</summary>
  /// <remarks>Нигде не применяется: снимок формируется при извлечении команд из очереди, поскольку для каждого
  /// топика в области подписки создаётся отдельное событие. Эти события относятся к фазе подписки и должны
  /// предшествовать остальным изменениям того же тика.</remarks>
  internal sealed class CmdSubscribe : Cmd {
    public readonly SubRec Sub;
    public CmdSubscribe(Topic target, SubRec sub) : base(target, target) { this.Sub = sub; }
    public override Phase Phase { get { return Phase.Sub; } }
    public override TopicEvent Apply() { return null; }
  }

  /// <summary>Сообщает, что подписка установлена, для уже существовавшей регистрации.</summary>
  internal sealed class CmdAck : Cmd {
    public readonly SubRec Sub;
    public CmdAck(Topic target, SubRec sub) : base(target, target) { this.Sub = sub; }
    public override Phase Phase { get { return Phase.Ack; } }
    public override TopicEvent Apply() { return TopicEvent.Ready(Target, Sub); }
  }
}
