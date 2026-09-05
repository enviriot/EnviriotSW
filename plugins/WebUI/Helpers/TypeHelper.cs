///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using X13.Repository;

namespace X13.WebUI.Helpers {
  internal static class TypeHelper {
    public const string TypeRootPath = "/$YS/TYPES";
    public const string TypeRootPrefix = TypeRootPath + "/";

    public static Topic ResolveTypeTopic(string typePath) {
      if(string.IsNullOrWhiteSpace(typePath)) return null;
      return Topic.root.Get(TypeRootPath, false)?.Get(StripTypeRoot(typePath), false);
    }

    public static string StripTypeRoot(string path) {
      if(string.IsNullOrWhiteSpace(path)) return path;
      if(path.StartsWith(TypeRootPrefix, System.StringComparison.OrdinalIgnoreCase)) return path.Substring(TypeRootPrefix.Length);
      return path;
    }
  }
}
