///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace X13.Repository {
  public class SubRec : IDisposable {
    public readonly Topic setTopic;
    public readonly SubMask mask;
    public readonly string prefix;
    internal readonly Action<Perform, SubRec> func;

    internal SubRec(Topic t, Action<Perform, SubRec> func, SubRec.SubMask mask, string prefix) {
      this.setTopic = t;
      this.func = func;
      this.mask = mask;
      this.prefix = (prefix==null && (mask & SubMask.Field)==SubMask.Field)?string.Empty:prefix;

    }
    public override string ToString() {
      return string.Format("{0}{1}{4} > {2}.{3}", setTopic.path, (mask & (SubMask.Chldren | SubMask.All)) != SubMask.None ? ((mask & SubMask.Chldren) != SubMask.None ? "/+" : "/#") : string.Empty,
        func.Target == null ? func.Method.DeclaringType.Name : func.Target.ToString(), func.Method.Name, (mask & SubMask.Field) != SubMask.None ? ("¤" + prefix) : string.Empty);
    }
    public void Dispose() {
      Topic.I.Unsubscribe(setTopic, this);
    }

    [Flags]
    public enum SubMask {
      None = 0,
      Once = 1,
      Chldren = 2,
      All = 4,
      OnceOrAll = 5,
      Value = 8,
      Field = 16,
    }
  }
}
