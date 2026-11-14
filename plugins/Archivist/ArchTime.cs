///<remarks>This file is part of the <see cref="https://github.com/enviriot">Enviriot</see> project.<remarks>
using System;

namespace X13.Archivist {
  /// <summary>Вычисления временных корзин и ключей архива без ввода-вывода и чтения часов.</summary>
  /// <remarks>Обоснование формата и шкалы UTC — в <see href="Archivist.md#ключи-хранения-и-временная-модель">общих правилах ключей и времени</see>.</remarks>
  internal static class ArchTime {
    /// <summary>Начало нумерации сохранённых корзин в UTC, независимое от местного часового пояса.</summary>
    internal static readonly DateTime Epoch = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    internal const int GRAN_RAW = 0;
    internal const int GRAN_5MIN = 300;
    internal const int GRAN_HOUR = 3600;
    internal const int GRAN_DAY = 86400;

    /// <summary>Уровни сохранённых свёрток, начиная с самых крупных корзин.</summary>
    internal static readonly int[] LEVELS = { GRAN_DAY, GRAN_HOUR, GRAN_5MIN };

    /// <summary>Выбирает самый крупный источник, сохраняющий запрошенное разрешение.</summary>
    /// <remarks>Корзина должна помещаться в один шаг запроса; иначе нужны более мелкие корзины или исходные отсчёты. 
    /// Здесь выбирается разрешение, а не проверяется готовность свёрток.</remarks>
    internal static int Granularity(double stepSeconds) {
      foreach(var g in LEVELS) {
        if(stepSeconds >= g) {
          return g;
        }
      }
      return GRAN_RAW;
    }

    internal static DateTime HourFloor(DateTime utc) {
      return new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, utc.Kind);
    }
    internal static DateTime DayFloor(DateTime utc) {
      return new DateTime(utc.Year, utc.Month, utc.Day, 0, 0, 0, utc.Kind);
    }

    internal static long HourIndex(DateTime utc) {
      return (long)Math.Floor((utc - Epoch).TotalHours);
    }
    internal static long DayIndex(DateTime utc) {
      return (long)Math.Floor((utc - Epoch).TotalDays);
    }
    internal static DateTime HourStart(long index) {
      return Epoch.AddHours(index);
    }
    internal static DateTime DayStart(long index) {
      return Epoch.AddDays(index);
    }

    /// <summary>Вычисляет номер корзины заданной длительности относительно Epoch.</summary>
    /// <remarks>Время должно быть в UTC, длительность — положительной и заданной в секундах.
    /// Для часа и суток используется та же нумерация, что в HourIndex и DayIndex; GRAN_RAW здесь не применяется.</remarks>
    internal static long BucketIndex(DateTime utc, int granularity) {
      return (long)Math.Floor((utc - Epoch).TotalSeconds / granularity);
    }
    internal static DateTime BucketStart(long index, int granularity) {
      return Epoch.AddSeconds((double)index * granularity);
    }
    /// <summary>Возвращает середину интервала корзины в UTC.</summary>
    internal static DateTime BucketMid(long index, int granularity) {
      return BucketStart(index, granularity).AddSeconds(granularity / 2.0);
    }

    /// <summary>Объединяет идентификатор топика и номер корзины в упорядоченный первичный ключ.</summary>
    /// <remarks>Старшие 32 бита отведены топику, младшие — корзине. 
    /// Проверки исключают отрицательные номера корзин (до Epoch) и выход за диапазон UInt32: приведение к uint не должно обрезать номер и создавать другой ключ.</remarks>
    internal static long PackId(int topicId, long bucket) {
      if(topicId < 0) throw new ArgumentOutOfRangeException("topicId", topicId, "topic id must not be negative");
      if(bucket < 0 || bucket > uint.MaxValue) throw new ArgumentOutOfRangeException("bucket", bucket, "bucket index outside the packable range");
      return ((long)topicId << 32) | (uint)bucket;
    }
    internal static int TopicOf(long packed) {
      return (int)(packed >> 32);
    }
    internal static long BucketOf(long packed) {
      return packed & 0xFFFFFFFFL;
    }
    /// <summary>Включительные границы первичных ключей всех корзин одного топика.</summary>
    internal static void TopicRange(int topicId, out long lo, out long hi) {
      lo = PackId(topicId, 0);
      hi = PackId(topicId, uint.MaxValue);
    }

    #region sample keys

    /// <summary>Размер ключа исходного отсчёта в байтах: идентификатор топика, затем время.</summary>
    internal const int KEY_LEN = 12;

    /// <summary>Кодирует топик и время исходного отсчёта в единый двоичный ключ.</summary>
    /// <remarks>Четыре байта отведены идентификатору, восемь — тикам времени без потери точности.
    /// Обе части записываются старшим байтом вперёд.
    /// <para>Время должно быть уже в UTC: перегрузка DateTime берёт Ticks без преобразования Kind.</para>
    /// <para>Разрешение в 100 нс не гарантирует уникальность: одинаковые топик и время дают одинаковый ключ.
    /// Обработка повторяющихся временных меток остаётся за вызывающей стороной.</para></remarks>
    internal static byte[] PackSample(int topicId, DateTime utc) {
      return PackSample(topicId, utc.Ticks);
    }

    internal static byte[] PackSample(int topicId, long ticks) {
      if(topicId < 0) {
        throw new ArgumentOutOfRangeException("topicId", topicId, "topic id must not be negative");
      }
      if(ticks < 0) {
        throw new ArgumentOutOfRangeException("ticks", ticks, "instant outside the representable range");
      }
      var b = new byte[KEY_LEN];
      b[0] = (byte)(topicId >> 24);
      b[1] = (byte)(topicId >> 16);
      b[2] = (byte)(topicId >> 8);
      b[3] = (byte)topicId;
      for(int i = 0; i < 8; i++) {
        b[4 + i] = (byte)(ticks >> (56 - i * 8));
      }
      return b;
    }

    /// <summary>Нижняя включительная граница ключей исходных отсчётов одного топика.</summary>
    internal static byte[] TopicFloor(int topicId) {
      return PackSample(topicId, 0L);
    }
    /// <summary>Верхняя включительная граница ключей отсчётов топика в диапазоне DateTime.</summary>
    internal static byte[] TopicCeil(int topicId) {
      return PackSample(topicId, DateTime.MaxValue.Ticks);
    }

    internal static int TopicOfSample(byte[] key) {
      return (key[0] << 24) | (key[1] << 16) | (key[2] << 8) | key[3];
    }

    /// <summary>Восстанавливает время отсчёта из ключа в представлении UTC.</summary>
    internal static DateTime TimeOfSample(byte[] key) {
      long t = 0;
      for(int i = 0; i < 8; i++) {
        t = (t << 8) | key[4 + i];
      }
      return new DateTime(t, DateTimeKind.Utc);
    }

    #endregion sample keys
  }
}
