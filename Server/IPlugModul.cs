///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using X13.Repository;

namespace X13 {
  /// <summary>A subsystem the server owns: loaded by MEF, driven by Program on the engine thread.</summary>
  /// <remarks>Init and Start run in ascending priority order and Stop in descending, all on the
  /// engine thread, so a plugin may assume every lower-priority plugin is already up.
  /// <para>The part of the contract that is easy to get wrong is what happens after a failure.
  /// Init and Start are allowed to throw: the server logs it and abandons the startup, and then
  /// stops everything it had got to - including the plugin that just threw, because Stop is the
  /// only teardown this interface has. So Stop can be handed a half-built object and must assume
  /// nothing about what Init managed to finish. Repo.Stop guards itself with a _loaded flag for
  /// exactly this reason, and it has that flag because the case turned up in practice.</para>
  /// <para>The reverse does not happen: Stop is not called on a plugin that is disabled or whose
  /// turn never came, so "Stop before Init" is not a state to defend against. Stop may itself
  /// throw - it is caught, logged with the state the plugin had reached, and the remaining
  /// plugins are stopped anyway - but a plugin that throws here is one that keeps whatever it was
  /// holding, so this is a poor place to be careless.</para>
  /// <para>There is deliberately no Dispose in this interface. MEF disposes any part that
  /// implements IDisposable when the container is torn down, which happens after Stop; adding a
  /// second teardown method would split one responsibility across two, with nothing to say which
  /// of them frees what.</para></remarks>
  public interface IPlugModul {
    /// <summary>Acquires what the plugin needs. May throw; see the type remarks.</summary>
    void Init();
    /// <summary>Begins work, with every lower-priority plugin already initialised and started.</summary>
    void Start();
    /// <summary>One pass of the engine loop, about sixty times a second.</summary>
    /// <remarks>A throw is caught and throttled, and the plugin goes on being ticked: stopping it
    /// would turn one bad pass into a subsystem that never runs again with nothing to revive it.
    /// Slow is as harmful as wrong here - the whole loop waits.</remarks>
    void Tick();
    /// <summary>Releases everything Init and Start acquired, from any state either reached.</summary>
    void Stop();

    /// <summary>The plugin's own node under /$YS, root of everything it configures.</summary>
    /// <remarks>Every implementation already kept this topic in a private field; it is here
    /// because <see cref="enabled"/> is read BEFORE Init() (Program.InitPlugins), so the getter
    /// has to resolve the topic itself rather than wait for Init to fill a field. Implementations
    /// are lazy for that reason - the property, not Init, is what guarantees a non-null value.
    /// </remarks>
    Topic Owner { get; }

    /// <summary>Whether the server runs this plugin at all, answered from <see cref="Owner"/>.</summary>
    /// <remarks>Read-only: switching a plugin off is done by writing false into its /$YS topic,
    /// which is Config-attributed and so survives a restart. The setter that used to mirror that
    /// write had no caller outside its own test - InitPlugins and StartPlugins only ever read it -
    /// and a second way to say the same thing is a second thing to keep in agreement.</remarks>
    bool enabled { get; }
  }
  public interface IPlugModulData {
    int priority { get; }
    string name { get; }
  }
}
