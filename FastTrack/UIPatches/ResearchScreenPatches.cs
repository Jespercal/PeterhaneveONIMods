/*
 * Copyright 2022 Peter Han
 * Permission is hereby granted, free of charge, to any person obtaining a copy of this software
 * and associated documentation files (the "Software"), to deal in the Software without
 * restriction, including without limitation the rights to use, copy, modify, merge, publish,
 * distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the
 * Software is furnished to do so, subject to the following conditions:
 *
 * The above copyright notice and this permission notice shall be included in all copies or
 * substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING
 * BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
 * NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM,
 * DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
 * FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
 */

using HarmonyLib;
using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

namespace PeterHan.FastTrack.UIPatches {
	/// <summary>
	/// Applied to ResearchScreen to make its dreadfully slow Update method way faster.
	/// </summary>
	[HarmonyPatch(typeof(ResearchScreen), "Update")]
	public static class ResearchScreen_Update_Patch {
		/// <summary>
		/// The squared threshold in pixels where movement ends.
		/// </summary>
		private const float THRESHOLD_SQ = 4.0f;

		internal static bool Prepare() => FastTrackOptions.Instance.VirtualScroll;

		private static Vector2 ClampBack(ResearchScreen __instance, RectTransform rt,
				float zoom, Vector2 inertia, Vector2 anchorPos) {
			Type info = __instance.GetType();
			const float ZS = 250.0f;
			Vector2 contentSize = rt.rect.size, target = (Vector2)info.GetField("forceTargetPosition", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(__instance);
			float y = 0.0f, xMin = (-contentSize.x * 0.5f - ZS) * zoom, xMax = ZS * zoom,
				yMin = -ZS * zoom;
			if (__instance.TryGetComponent(out RectTransform irt))
				y = irt.rect.size.y;
			float yMax = (contentSize.y + ZS) * zoom - y;
			target.x = Mathf.Clamp(target.x, xMin, xMax);
			target.y = Mathf.Clamp(target.y, yMin, yMax);
			Vector2 deltaAnchor = new Vector2(Mathf.Clamp(anchorPos.x, xMin, xMax),
				Mathf.Clamp(anchorPos.y, yMin, yMax)) + inertia - anchorPos;
            info.GetField("forceTargetPosition", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(__instance, target);
			return deltaAnchor;
		}

		/// <summary>
		/// Applied before Update runs.
		/// </summary>
		internal static bool Prefix(ResearchScreen __instance, ref Vector2 ___dragInteria, ref Vector3 ___dragLastPosition, float ___edgeClampFactor, ref Vector2 ___keyPanDelta) {
			if (__instance.canvas.enabled && __instance.scrollContent.TryGetComponent(
					out RectTransform rt)) {
				Vector2 anchorPos = rt.anchoredPosition, startPos = anchorPos, inertia =
                    ___dragInteria;
				Vector3 mousePos = KInputManager.GetMousePos();
				bool dragging = UpdateDrag(__instance, mousePos);
				float dt = Time.unscaledDeltaTime;
				// Update from user input
				float zoom = UpdateZoom(__instance, mousePos, rt, dt, ref anchorPos);
				bool anyDown = UpdateKeyboard(__instance, dt, ref anchorPos,
					out Vector2 keyDelta);
				if (dragging) {
					Vector2 inerDelta = mousePos - ___dragLastPosition;
					anchorPos += inerDelta;
                    ___dragLastPosition = mousePos;
					inertia = Vector2.ClampMagnitude(inertia + inerDelta, 400.0f);
				}
				inertia *= Math.Max(0.0f, 1.0f - dt * 4.0f);
                ___dragInteria = inertia;
				// Slide view back in bounds if not dragging
				if (!dragging) {
					Vector2 deltaAnchor = ClampBack(__instance, rt, zoom, inertia, anchorPos);
					if (anyDown) {
						// Zero out keyboard input vectors at edge
						anchorPos += deltaAnchor;
						if (deltaAnchor.x < 0f)
							keyDelta.x = Math.Min(0f, keyDelta.x);
						if (deltaAnchor.x > 0f)
							keyDelta.x = Math.Max(0f, keyDelta.x);
						if (deltaAnchor.y < 0f)
							keyDelta.y = Math.Min(0f, keyDelta.y);
						if (deltaAnchor.y > 0f)
							keyDelta.y = Math.Max(0f, keyDelta.y);
					} else
						anchorPos += deltaAnchor * ___edgeClampFactor * dt;
				}
                ___keyPanDelta = keyDelta;
				ZoomToTarget(__instance, dt, anyDown || dragging, ref anchorPos);
				if (!Mathf.Approximately(anchorPos.x, startPos.x) || !Mathf.Approximately(
						anchorPos.y, startPos.y))
					rt.anchoredPosition = anchorPos;
			}
			return false;
		}

		/// <summary>
		/// Updates the drag state of the research screen.
		/// </summary>
		/// <param name="instance">The research screen instance to update.</param>
		/// <param name="mousePos">The current mouse position.</param>
		/// <returns>true if the user is dragging the screen, or false otherwise.</returns>
		private static bool UpdateDrag(ResearchScreen instance, Vector3 mousePos) {
			Type info = instance.GetType();

            bool dragging = (bool)info.GetField("isDragging", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(instance), buttonDown = (bool)info.GetField("leftMouseDown", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(instance) ||
				(bool)info.GetField("rightMouseDown", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(instance);
			// No square root for you, sqrt(1) = 1
			if (!dragging && buttonDown && (((Vector3)info.GetField("dragStartPosition", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(instance)) - mousePos).
					sqrMagnitude > THRESHOLD_SQ)
				dragging = true;
			else if (dragging && !buttonDown) {
                info.GetField("leftMouseDown", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(instance, false);
                info.GetField("rightMouseDown", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(instance, false);
                dragging = false;
			}
			info.GetField("isDragging", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(instance, dragging);
			return dragging;
		}

		/// <summary>
		/// Updates the keyboard input for panning the research screen.
		/// </summary>
		/// <param name="instance">The research screen to pan.</param>
		/// <param name="dt">The unscaled delta time since the last update.</param>
		/// <param name="anchorPos">The position of the tech tree to update.</param>
		/// <param name="delta">The rate at which the keyboard is moving the view.</param>
		/// <returns>true if any keyboard keys are down, or false otherwise.</returns>
		private static bool UpdateKeyboard(ResearchScreen instance, float dt,
				ref Vector2 anchorPos, out Vector2 delta) {
			Type info = instance.GetType();

			float speed = (float)info.GetField("keyboardScrollSpeed", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(instance), easing = (float)info.GetField("keyPanEasing", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(instance);
			Vector2 panDelta = (Vector2)info.GetField("keyPanDelta", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(instance);
			bool anyDown = false;
			if ((bool)info.GetField("panUp", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(instance)) {
				panDelta.y -= dt * speed;
				anyDown = true;
			} else if ((bool)info.GetField("panDown", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(instance)) {
				panDelta.y += dt * speed;
				anyDown = true;
			}
			if ((bool)info.GetField("panLeft", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(instance)) {
				panDelta.x += dt * speed;
				anyDown = true;
			} else if ((bool)info.GetField("panRight", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(instance)) {
				panDelta.x -= dt * speed;
				anyDown = true;
			}
			// Steam Controller/Deck support
			if (KInputManager.currentControllerIsGamepad) {
				panDelta = KInputManager.steamInputInterpreter.GetSteamCameraMovement() *
					dt * speed * -5.0f;
				anyDown = true;
			}
			// Deceleration
			panDelta.x -= Mathf.Lerp(0f, panDelta.x, dt * easing);
			panDelta.y -= Mathf.Lerp(0f, panDelta.y, dt * easing);
			info.GetField("keyPanDelta", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(instance, panDelta);

            anchorPos += panDelta;
			delta = panDelta;
			return anyDown;
		}

		/// <summary>
		/// Updates the zoom state of the research screen.
		/// </summary>
		/// <param name="instance">The research screen instance to update.</param>
		/// <param name="mousePos">The current mouse position.</param>
		/// <param name="rt">The transform with the displayed tech tree.</param>
		/// <param name="dt">The unscaled delta time since the last update.</param>
		/// <param name="anchorPos">The position of the tech tree to update.</param>
		/// <returns>The current zoom level.</returns>
		private static float UpdateZoom(ResearchScreen instance, Vector3 mousePos,
				RectTransform rt, float dt, ref Vector2 anchorPos) {
			Type info = instance.GetType();

			float zoom = (float)info.GetField("currentZoom", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(instance), oldZoom = zoom;
			Vector2 target = mousePos;
			zoom = Mathf.Lerp(zoom, (float)info.GetField("targetZoom", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(instance), Math.Min(0.9f, (float)info.GetField("effectiveZoomSpeed", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(instance) * dt));
			info.GetField("currentZoom", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(instance, zoom);
            if ((bool)info.GetField("zoomCenterLock", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(instance))
				target = new Vector2(0.5f * Screen.width, 0.5f * Screen.height);
			Vector2 before = zoom * rt.InverseTransformPoint(target);
			if (!Mathf.Approximately(zoom, oldZoom))
				rt.localScale = new Vector3(zoom, zoom, 1.0f);
			anchorPos += (Vector2)rt.InverseTransformPoint(target) * zoom - before;
			return zoom;
		}

		/// <summary>
		/// Zooms to the target coordinates if necessary.
		/// </summary>
		/// <param name="instance">The research screen instance to zoom.</param>
		/// <param name="dt">The unscaled delta time since the last update.</param>
		/// <param name="input">Whether user input is currently occurring.</param>
		/// <param name="anchorPos">The position of the tech tree to update.</param>
		private static void ZoomToTarget(ResearchScreen instance, float dt, bool input,
				ref Vector2 anchorPos) {
			Vector2 target = (Vector2)instance.GetType().GetField("forceTargetPosition", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(instance), pos = anchorPos;
			if ((bool)instance.GetType().GetField("zoomingToTarget", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(instance)) {
				// Process automatic zoom in, cancel if user input occurs
				pos = Vector2.Lerp(pos, target, dt * 4.0f);
				if ((pos - target).sqrMagnitude < THRESHOLD_SQ || input)
					instance.GetType().GetField("zoomingToTarget", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(instance, false);
				anchorPos = pos;
			}
		}
	}

	/// <summary>
	/// Applied to ResearchScreen to update the canvas size only when it is shown.
	/// </summary>
	[HarmonyPatch(typeof(ResearchScreen), "OnShow")]
	public static class ResearchScreen_OnShow_Patch {
		internal static bool Prepare() => FastTrackOptions.Instance.VirtualScroll;

		/// <summary>
		/// Freezes all of the top level layouts in the children of the specified object.
		/// </summary>
		/// <param name="components">The object containing the children to freeze. Please do not freeze children IRL.</param>
		private static System.Collections.IEnumerator FreezeLayouts(Transform components) {
			yield return null;
			if (components != null) {
				int n = components.childCount;
				for (int i = 0; i < n; i++) {
					var child = components.GetChild(i);
					GameObject go;
					// Only handle active children
					if (child != null && (go = child.gameObject).activeSelf && go.
							TryGetComponent(out LayoutGroup realLayout)) {
						var frozenLayout = go.AddOrGet<LayoutElement>();
						frozenLayout.CopyFrom(realLayout);
						frozenLayout.layoutPriority = 100;
						frozenLayout.enabled = true;
						realLayout.enabled = false;
					}
				}
			}
		}

		/// <summary>
		/// Applied after OnShow runs.
		/// </summary>
		internal static void Postfix(ResearchScreen __instance, bool show) {
			var content = __instance.scrollContent;
			if (show && content != null && __instance.isActiveAndEnabled) {
				if (content.TryGetComponent(out KChildFitter cf))
					cf.FitSize();
				__instance.StartCoroutine(FreezeLayouts(content.transform));
			}
		}
	}
}
