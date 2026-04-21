using System.Collections.Generic;
using UnityEngine;

namespace Game.Minigames
{
    /// <summary>
    /// Lightweight visual controller for runtime-created oil stains.
    /// </summary>
    public class OilStainView : MonoBehaviour
    {
        [SerializeField] [Min(0.01f)] private float _minScaleFactor = 0.55f;
        [SerializeField] [Min(0.01f)] private float _completionScaleFactor = 0.2f;
        [SerializeField] [Min(0.01f)] private float _completionFadeSeconds = 0.2f;
        [SerializeField] [Range(0.5f, 0.98f)] private float _fadeStartThreshold = 0.82f;
        [SerializeField] [Range(0f, 1f)] private float _hoverTintStrength = 0.2f;
        [SerializeField] [Range(0f, 1f)] private float _lateColorShiftStrength = 0.12f;
        [SerializeField] [Range(0f, 1f)] private float _lateFadeAlphaFloor = 0.5f;

        private Renderer[] _renderers;
        private Material[] _runtimeMaterials;
        private Color _dirtyColor = Color.black;
        private Color _hoverColor = Color.white;
        private Color _cleanColor = new Color(0f, 0f, 0f, 0f);
        private Vector3 _baseScale = Vector3.one;
        private Coroutine _completionRoutine;
        private bool _isCompleted;

        public void Initialize(Renderer stainRenderer, Color dirtyColor, Color hoverColor, Color cleanColor)
        {
            Initialize(stainRenderer != null ? new[] { stainRenderer } : null, dirtyColor, hoverColor, cleanColor);
        }

        public void Initialize(Renderer[] stainRenderers, Color dirtyColor, Color hoverColor, Color cleanColor)
        {
            _renderers = stainRenderers;
            _dirtyColor = dirtyColor;
            _hoverColor = hoverColor;
            _cleanColor = cleanColor;
            _baseScale = transform.localScale;
            _runtimeMaterials = BuildRuntimeMaterials(_renderers);

            _isCompleted = false;
        }

        public void SetProgress01(float progress01, bool hovered)
        {
            if (_isCompleted)
            {
                return;
            }

            float t = Mathf.Clamp01(progress01);
            float scaleFactor = Mathf.Lerp(1f, _minScaleFactor, t);
            transform.localScale = _baseScale * scaleFactor;

            if (_runtimeMaterials == null || _runtimeMaterials.Length <= 0)
            {
                return;
            }

            float fadeT = t < _fadeStartThreshold
                ? 0f
                : Mathf.InverseLerp(_fadeStartThreshold, 1f, t);

            Color color = Color.Lerp(_dirtyColor, _cleanColor, fadeT * _lateColorShiftStrength);
            if (hovered)
            {
                color = Color.Lerp(color, _hoverColor, _hoverTintStrength);
            }

            float targetAlpha = Mathf.Max(0.05f, _dirtyColor.a * Mathf.Clamp01(_lateFadeAlphaFloor));
            color.a = Mathf.Lerp(_dirtyColor.a, targetAlpha, fadeT);
            SetColorOnMaterials(color);
        }

        public void PlayCompleteAndHide()
        {
            if (_isCompleted)
            {
                return;
            }

            _isCompleted = true;
            if (_completionRoutine != null)
            {
                StopCoroutine(_completionRoutine);
            }

            _completionRoutine = StartCoroutine(PlayCompleteAndHideRoutine());
        }

        private System.Collections.IEnumerator PlayCompleteAndHideRoutine()
        {
            Vector3 startScale = transform.localScale;
            Vector3 endScale = _baseScale * _completionScaleFactor;
            Color startColor = GetCurrentMaterialColorOrDefault();
            Color endColor = startColor;
            endColor.a = 0f;

            float duration = Mathf.Max(0.01f, _completionFadeSeconds);
            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / duration);
                transform.localScale = Vector3.Lerp(startScale, endScale, t);

                SetColorOnMaterials(Color.Lerp(startColor, endColor, t));

                yield return null;
            }

            gameObject.SetActive(false);
            _completionRoutine = null;
        }

        private static Material[] BuildRuntimeMaterials(Renderer[] renderers)
        {
            if (renderers == null || renderers.Length <= 0)
            {
                return null;
            }

            List<Material> materials = new List<Material>(renderers.Length);
            Shader preferredShader = Shader.Find("Unlit/Transparent");
            if (preferredShader == null)
            {
                preferredShader = Shader.Find("Sprites/Default");
            }

            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null)
                {
                    continue;
                }

                Material source = renderer.material;
                if (source == null)
                {
                    continue;
                }

                Material runtimeMaterial = preferredShader != null
                    ? new Material(preferredShader)
                    : source;

                if (runtimeMaterial != source)
                {
                    runtimeMaterial.name = $"{source.name}_OilStainRuntime";
                }

                renderer.material = runtimeMaterial;
                materials.Add(runtimeMaterial);
            }

            return materials.ToArray();
        }

        private void SetColorOnMaterials(Color color)
        {
            if (_runtimeMaterials == null)
            {
                return;
            }

            for (int i = 0; i < _runtimeMaterials.Length; i++)
            {
                Material material = _runtimeMaterials[i];
                if (material != null)
                {
                    material.color = color;
                }
            }
        }

        private Color GetCurrentMaterialColorOrDefault()
        {
            if (_runtimeMaterials != null)
            {
                for (int i = 0; i < _runtimeMaterials.Length; i++)
                {
                    Material material = _runtimeMaterials[i];
                    if (material != null)
                    {
                        return material.color;
                    }
                }
            }

            return _cleanColor;
        }
    }
}
