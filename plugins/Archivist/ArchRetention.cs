///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using System;
using X13.Repository;
using NiL.JS.Extensions;

namespace X13.Archivist {

  /// <summary>Удаляет просроченные отсчёты и свёртки небольшими шагами.</summary>
  /// <remarks>Очистка не прореживает исходные отсчёты и не заменяет их средними: данные удаляются по сроку хранения. 
  /// Уменьшение размера файлов выполняется отдельно.</remarks>
  internal static class ArchRetention {
    /// <summary>Максимальная ширина окна очистки исходных отсчётов за один шаг.</summary>
    /// <remarks>Ограничивает продвижение курсора, а не число записей или длительность прохода.</remarks>
    internal static readonly TimeSpan STEP = TimeSpan.FromDays(1);

    /// <summary>Границы порога накопленного просроченного интервала перед очисткой.</summary>
    /// <remarks>Граница удаления движется вместе с часами. Без нижнего порога каждый обход мог бы запускать очистку почти пустого окна и лишнее сохранение курсора.</remarks>
    private static readonly TimeSpan MIN_GAP = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MAX_GAP = TimeSpan.FromHours(1);

    /// <summary>Определяет порог отставания курсора от границы удаления для действующего ряда.</summary>
    /// <remarks>Четверть срока хранения, ограниченная минутой и часом, позволяет чаще очищать короткие ряды и реже обслуживать длинную историю. 
    /// Это не таймер: накопившаяся просроченная история может очищаться несколькими проходами подряд.</remarks>
    private static TimeSpan Gap(ArchTopic at) {
      double sec = at.Keep * 24 * 3600 / 4;
      if(sec < MIN_GAP.TotalSeconds) return MIN_GAP;
      return sec > MAX_GAP.TotalSeconds ? MAX_GAP : TimeSpan.FromSeconds(sec);
    }

    /// <summary>Продвигает курсор очистки одного ряда не более чем на одно окно.</summary>
    /// <returns>True, если выполнен шаг очистки или удалён ряд, даже если записей в окне не было.</returns>
    internal static bool Step(ArchStore st, ArchTopic at) {
      if(st == null || !st.IsOpen || at == null) return false;
      DateTime nowUtc = DateTime.UtcNow;
      bool orphan = IsOrphan(at);
      // Для отсутствующего, удалённого или отключённого топика срок хранения не действует: очищаем историю до текущего времени, затем удаляем ряд из реестра.
      DateTime cutoff = orphan ? nowUtc : nowUtc.AddDays(-at.Keep);

      if(at.Pt == DateTime.MinValue) {
        // Если курсор очистки ещё не задан, используем курсор свёртки. При отсутствии обоих начинаем с текущего времени, не сканируя историю для поиска начала.
        at.Pt = at.Rt == DateTime.MinValue ? nowUtc : at.Rt;
        st.Store(at);
        return false;
      }
      if(at.Pt >= cutoff) {
        if(orphan) {
          st.DropTopic(at);
          return true;
        }
        return false;
      }
      // Не запускаем запись ради слишком малого просроченного интервала. Для ненужного ряда порог не применяется: его историю нужно удалить полностью.
      if(!orphan && cutoff - at.Pt < Gap(at)) {
        return false;
      }
      DateTime hi = at.Pt + STEP < cutoff ? at.Pt + STEP : cutoff;
      st.PurgeRaw(at, at.Pt.ToLocalTime(), hi.ToLocalTime());
      // Корзины удаляются целиком, до корзины, содержащей hi. Диапазон начинается с нуля, чтобы не оставить старые свёртки при пропуске участка курсором.
      foreach(var g in ArchTime.LEVELS) {
        st.PurgeRoll(g, at.Id, 0, ArchTime.BucketIndex(hi, g) - 1);
      }
      at.Pt = hi;
      if(orphan && hi >= cutoff) {
        // Удаляем остатки данных и запись реестра в том же проходе, который достиг cutoff.
        // При следующем вызове текущее время сдвинется, и курсор снова окажется позади: ожидание только проверки в начале метода не завершило бы удаление ряда.
        st.DropTopic(at);
        return true;
      }
      st.Store(at);
      return true;
    }

    /// <summary>Определяет, отсутствует ли топик, удалён ли он или отключено его архивирование.</summary>
    private static bool IsOrphan(ArchTopic at) {
      return at.T == null || at.T.disposed || !at.T.GetField("Arch.enable").As<bool>();
    }
  }
}
