///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using LiteDB;
using NiL.JS.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace X13.Repository {
  public class Perform : IComparable<Perform> {

    internal static Perform Create(Topic src, Art art, Topic prim) {
      Perform r;
      r = new Perform(art, src, prim);
      r.o = null;
      r.i = 0;
      return r;
    }
    internal static Perform Create(Topic src, JSValue val, Topic prim) {
      Perform r;
      r = new Perform(Art.setState, src, prim);
      r.o = val;
      r.i = 0;
      return r;
    }
    internal static Perform Create(Topic src, string fName, JSValue val, Topic prim) {
      Perform r;
      r = new Perform(Art.setField, src, prim);
      r.o = fName;
      r.f_v = val;
      r.i = 0;
      return r;
    }

    internal object o;
    internal int i;
    internal object old_o;
    internal JSValue f_v;

    public readonly Topic src;
    public Topic prim { get; internal set; }
    public readonly int layer;
    public Art art { get; internal set; }
    public string FieldPath { get { return this.art == Art.changedField ? (o as string) : null; } }

    private Perform(Art art, Topic src, Topic prim) {
      this.src = src;
      this.art = art;
      this.prim = prim;
      this.layer = src.layer;
    }
    internal bool EqualsGr(Perform other) {
      return (this.art == Art.setState || this.art == Art.changedState)
        && other != null
        && this.src == other.src
        && (((int)this.art) >> 2) == (((int)other.art) >> 2);
    }
    public int CompareTo(Perform other) {
      if(other == null) {
        return -1;
      }
      int p1 = ((int)this.art) >> 2;
      int p2 = (int)(other.art) >> 2;
      if(p1 != p2) {
        return p1.CompareTo(p2);
      }
      if(this.layer != other.layer) {
        return this.layer > other.layer ? 1 : -1;
      }
      if(this.src == other.src && (this.art == Art.setState || this.art == Art.changedState)) {
        return 0;
      }
      return -1;  // сохраняется порядок поступления
    }
    public override string ToString() {
      return string.Concat(src.path, "[", art.ToString(), ", ", layer.ToString(), "]=", o == null ? "null" : o.ToString());
    }

    public enum Art {
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
      changedLayer = 28,
    }
  }
}
