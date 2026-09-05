///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using X13.Repository;

namespace X13.WebUI.Helpers {
  /// <summary>Whether a client-driven write may touch a topic.</summary>
  /// <remarks>Only the topic-level half of the check lives here. The field-level half needs the
  /// Fields manifest walk, which belongs to the state tree and stays there - see
  /// StateTreeController.CheckWritable, which layers it on top of this.
  /// <para>Returns a reason string rather than a ViewOpResult so that Helpers keeps no dependency
  /// on the view layer at all; the two callers turn it into an error themselves. This check used
  /// to live on StateTreeController, which meant the Workspace tree reached into the state
  /// tree for it - one of the two calls that stopped the vertical split.</para>
  /// <para>Not applied to writes made by plugins: they seed their own readonly $YS topics through
  /// the very same Topic API and must keep working. Only client-driven paths honour the flag -
  /// which the UI already greys the row out for, so this refuses nothing a normal session would
  /// attempt. The catalog's remove path is deliberately exempt: every topic it owns is Required,
  /// so honouring that flag there would make it unable to uninstall anything.</para></remarks>
  internal static class WritePermission {
    internal const string ReadonlyCode = "target_readonly";

    /// <returns>Null when the topic may be written, otherwise why it may not.</returns>
    internal static string CheckTopic(Topic topic) {
      if(topic == null) return null;
      return topic.CheckAttribute(Topic.Attribute.Readonly) ? "Topic is readonly: " + topic.path : null;
    }
  }
}
