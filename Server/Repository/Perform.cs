///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using LiteDB;
using NiL.JS.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace X13.Repository {
  public class Perform : IComparable<Perform> {

    internal static Perform Create(Topic src, ArtEnum art, Topic prim) {
      return new Perform(art, src, prim) { o = null, i = 0 };
    }
    internal static Perform Create(Topic src, JSValue val, Topic prim) {
      return new Perform(ArtEnum.setState, src, prim) { o = val, i = 0 };
    }
    internal static Perform Create(Topic src, string fName, JSValue val, Topic prim) {
      return new Perform(ArtEnum.setField, src, prim) { o = fName, f_v = val, i = 0 };
    }

    internal object o;
    internal int i;
    internal object old_o;
    internal JSValue f_v;

    public readonly Topic src;
    public Topic Prim { get; internal set; }
    public ArtEnum Art { get; internal set; }
    public string FieldPath { get { return this.Art == ArtEnum.changedField ? (o as string) : null; } }

    private Perform(ArtEnum art, Topic src, Topic prim) {
      this.src = src;
      this.Art = art;
      this.Prim = prim;
    }
    internal bool EqualsGr(Perform other) {
      return (this.Art == ArtEnum.setState || this.Art == ArtEnum.changedState)
        && other != null
        && this.src == other.src
        && (((int)this.Art) >> 2) == (((int)other.Art) >> 2);
    }
    public int CompareTo(Perform other) {
      if(other == null) {
        return -1;
      }
      int p1 = ((int)this.Art) >> 2;
      int p2 = (int)(other.Art) >> 2;
      if(p1 != p2) {
        return p1.CompareTo(p2);
      }
      if(this.src == other.src && (this.Art == ArtEnum.setState || this.Art == ArtEnum.changedState)) {
        return 0;
      }
      return -1;  // сохраняется порядок поступления
    }
    public override string ToString() {
      return string.Concat(src.Path, "[", Art.ToString(), "]=", o == null ? "null" : o.ToString());
    }

    public enum ArtEnum {
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
