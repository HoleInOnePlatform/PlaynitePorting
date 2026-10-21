using System;
using System.Collections.Generic;
using System.Linq;

namespace Playnite.HoleInOne
{
    public sealed class StorageCandidate
    {
        public Guid Id { get; set; }
        public ulong Bytes { get; set; }
        public DateTime? LastPlayed { get; set; }
        public bool OnTargetDrive { get; set; }
        public bool IsProtected { get; set; }
    }

    public static class StoragePlanner
    {
        // Pure preflight: failure never returns a partial deletion list.
        public static List<StorageCandidate> Plan(ulong used, ulong required, ulong maximum,
            ulong freeOnTargetDrive, IEnumerable<StorageCandidate> candidates)
        {
            if (required == 0) throw new InvalidOperationException("게임 설치 용량을 확인하지 못했습니다. 라이브러리를 새로고침한 뒤 다시 시도해 주세요.");
            if (required > maximum) throw new InvalidOperationException("게임 용량이 최대 저장공간보다 큽니다. 설정에서 최대 용량을 늘려 주세요. 게임은 제거하지 않았습니다.");
            var quotaNeeded = used > maximum - required ? used - (maximum - required) : 0;
            var diskNeeded = required > freeOnTargetDrive ? required - freeOnTargetDrive : 0;
            var result = new List<StorageCandidate>();
            foreach (var item in candidates.Where(g => !g.IsProtected).OrderBy(g => g.LastPlayed ?? DateTime.MinValue).ThenBy(g => g.Id))
            {
                if (quotaNeeded == 0 && diskNeeded == 0) break;
                if (item.Bytes == 0 || (quotaNeeded == 0 && !item.OnTargetDrive)) continue;
                result.Add(item);
                quotaNeeded -= Math.Min(quotaNeeded, item.Bytes);
                if (item.OnTargetDrive) diskNeeded -= Math.Min(diskNeeded, item.Bytes);
            }
            if (quotaNeeded != 0 || diskNeeded != 0)
                throw new InvalidOperationException("제거 가능한 게임을 모두 정리해도 공간이 부족합니다. 최대 용량 또는 드라이브 여유 공간을 확인해 주세요. 게임은 제거하지 않았습니다.");
            return result;
        }
    }
}
