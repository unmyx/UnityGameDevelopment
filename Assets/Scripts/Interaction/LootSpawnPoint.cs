using UnityEngine;

namespace Game.Interaction
{
    /// <summary>
    /// Scene-authored loot spawn point keyed by loot type and stable point ID.
    /// </summary>
    public class LootSpawnPoint : MonoBehaviour
    {
        [SerializeField]
        private string _pointId = "point_01";

        [SerializeField]
        private string _lootTypeId = string.Empty;

        [Header("Debug")]
        [SerializeField]
        private bool _drawGizmo = true;

        [SerializeField]
        private Color _gizmoColor = new Color(1f, 0.75f, 0.2f, 1f);

        public string PointId => string.IsNullOrWhiteSpace(_pointId) ? string.Empty : _pointId.Trim();
        public string LootTypeId => string.IsNullOrWhiteSpace(_lootTypeId) ? string.Empty : _lootTypeId.Trim();

        private void OnValidate()
        {
            if (!string.IsNullOrWhiteSpace(_pointId))
            {
                _pointId = _pointId.Trim();
            }

            if (!string.IsNullOrWhiteSpace(_lootTypeId))
            {
                _lootTypeId = _lootTypeId.Trim();
            }
        }

        private void OnDrawGizmos()
        {
            if (!_drawGizmo)
            {
                return;
            }

            Gizmos.color = _gizmoColor;
            Gizmos.DrawWireSphere(transform.position, 0.09f);
            Gizmos.DrawLine(transform.position, transform.position + (transform.up * 0.2f));
        }
    }
}
