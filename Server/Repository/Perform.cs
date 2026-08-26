///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using NiL.JS.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace X13.Repository {
  public class Perform : IComparable<Perform> {

    internal static Perform Create(Topic src, E_Art art, Topic prim) {
      return new Perform(art, src, prim) { o = null, i = 0 };
    }
    internal static Perform Create(Topic src, JSValue val, Topic prim) {
      return new Perform(E_Art.setState, src, prim) { o = val, i = 0 };
    }
    internal static Perform Create(Topic src, string fName, JSValue val, Topic prim) {
      return new Perform(E_Art.setField, src, prim) { o = fName, f_v = val, i = 0 }; ;
    }

    /// <summary>Untyped payload whose meaning depends on <see cref="Art"/>.</summary>
    /// <remarks>Readable from outside the assembly since WebUI moved to plugins\, written only
    /// here. What it holds, per Art: <c>move</c> - the path the topic had BEFORE the move (a
    /// string; the only case a plugin needs today, since the new path is already on src);
    /// <c>setState</c> - the JSValue being set; <c>setField</c> - the field name (string), with
    /// the value in f_v; <c>subscribe</c>/<c>subAck</c>/<c>unsubscribe</c> - the SubRec; otherwise
    /// null. A subscriber that reads this must check Art first - `o as T` on the wrong Art
    /// silently yields null rather than failing.</remarks>
    public object o { get; internal set; }
    internal int i;
    internal object old_o;
    internal JSValue f_v;

    public readonly Topic src;
    public Topic Prim { get; internal set; }
    public E_Art Art { get; internal set; }
    public string FieldPath { get { return this.Art == E_Art.changedField ? (o as string) : null; } }

    private Perform(E_Art art, Topic src, Topic prim) {
      this.src = src;
      this.Art = art;
      this.Prim = prim;
    }
    internal bool EqualsGr(Perform other) {
      return (this.Art == E_Art.setState || this.Art == E_Art.changedState)
        && other != null
        && this.src == other.src
        && (((int)this.Art) >> 2) == (((int)other.Art) >> 2);
    }
    /// <summary>Ordering within a priority bucket, tuned for Repo.EnquePerf and nothing else.</summary>
    /// <remarks>This deliberately breaks IComparable's antisymmetry: for two Performs in the same
    /// bucket that are not the same-source setState/changedState pair, both a.CompareTo(b) and
    /// b.CompareTo(a) return -1, so that BinarySearch appends and arrival order is preserved.
    /// Consequences: _prOp must never be reordered with a plain Sort, and EnquePerf must stay the
    /// only insertion path - any other caller would get an order BinarySearch cannot reason about.</remarks>
    public int CompareTo(Perform other) {
      if(other == null) {
        return -1;
      }
      int p1 = ((int)this.Art) >> 2;
      int p2 = (int)(other.Art) >> 2;
      if(p1 != p2) {
        return p1.CompareTo(p2);
      }
      if(this.src == other.src && (this.Art == E_Art.setState || this.Art == E_Art.changedState)) {
        return 0;
      }
      return -1;  // сохраняется порядок поступления
    }
    public override string ToString() {
      return string.Concat(src.path, "[", Art.ToString(), "]=", o == null ? "null" : o.ToString());
    }

    public enum E_Art {
      move = 1,
      create = 2,
      subscribe = 4,
      unsubscribe = 8,
      setField = 12,
      changedField = 14,
      setState = 16,
      changedState = 18,
      remove = 20,
      subAck = 24,
    }
  }
}
