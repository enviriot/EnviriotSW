///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using X13.Repository;

namespace X13.WebUI.Helpers {
  /// <summary>Where the package catalog is fetched from.</summary>
  /// <remarks>Configuration, not view work, which is why it sits here rather than in the catalog
  /// view: both the catalog itself and the Workspace/Children "Catalog" menu command need the
  /// URI, and while this lived on CatalogViewProvider the tree controller had to reach up into a
  /// view provider to get it - the one call that stopped Catalog from being a self-contained
  /// block.</remarks>
  internal static class CatalogSettings {
    private const string DefaultCatalogUri = "https://enviriot.github.io/catalog/";

    /// <summary>The configured catalog URI, creating and seeding the config topic on first use.</summary>
    internal static string EnsureUri() {
      Topic catalog = Topic.root.Get("/$YS/Catalog", true);
      catalog.SetAttribute(Topic.Attribute.Required | Topic.Attribute.Config);
      Topic uri = catalog.Get("uri", true);
      // Readonly: the catalog's download path feeds this URL straight into Repo.Import, so the
      // catalog source is not something a client should be able to repoint.
      uri.SetAttribute(Topic.Attribute.Required | Topic.Attribute.Readonly | Topic.Attribute.Config);
      string value = uri.GetState().AsString(null);
      if(string.IsNullOrWhiteSpace(value)) {
        value = DefaultCatalogUri;
        uri.SetState(value);
      }
      return value;
    }
  }
}
