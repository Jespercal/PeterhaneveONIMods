﻿/*
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
using PeterHan.PLib.Core;
using System;
using System.Reflection.Emit;
using UnityEngine;

using TranspiledMethod = System.Collections.Generic.IEnumerable<HarmonyLib.CodeInstruction>;

namespace PeterHan.FastTrack.VisualPatches {
	/// <summary>
	/// Applied to AnimEventHandler to shave off a bit of time on repeated component lookups.
	/// </summary>
	[HarmonyPatch(typeof(AnimEventHandler), nameof(AnimEventHandler.UpdateOffset))]
	public static class AnimEventHandler_UpdateOffset_Patch {
		internal static bool Prepare() => FastTrackOptions.Instance.AnimOpts;

		/// <summary>
		/// Applied before UpdateOffset runs.
		/// </summary>
		internal static bool Prefix(AnimEventHandler __instance) {
			var navigator = __instance.navigator;
			var pivotSymbolPosition = __instance.controller.GetPivotSymbolPosition();
			var offset = navigator.NavGrid.GetNavTypeData(navigator.CurrentNavType).
				animControllerOffset;
			var baseOffset = __instance.baseOffset;
			var pos = __instance.transform.position;
			// Is the minus on x a typo? (Or is the plus on y the typo?)
			__instance.animCollider.offset = new Vector2(baseOffset.x + pivotSymbolPosition.x -
				pos.x - offset.x, baseOffset.y + pivotSymbolPosition.y - pos.y + offset.y);
			__instance.isDirty = Mathf.Max(0, __instance.isDirty - 1);
			return false;
		}
	}

	/// <summary>
	/// Applied to KAnimControllerBase to report anims paused by TAC as still playing to
	/// avoid breaking OnAnimQueueComplete.
	/// </summary>
	[HarmonyPatch(typeof(KAnimControllerBase), nameof(KAnimControllerBase.IsStopped))]
	public static class KAnimControllerBase_IsStopped_Patch {
		internal static bool Prepare() => FastTrackOptions.Instance.AnimOpts;

		/// <summary>
		/// Applied before IsStopped runs.
		/// </summary>
		internal static bool Prefix(KAnimControllerBase __instance, ref bool __result) {
			bool stopped = __instance.stopped;
			if (stopped) {
				var anim = __instance.GetCurrentAnim();
				var mode = __instance.GetMode();
				var inst = KAnimLoopOptimizer.Instance;
				// The desalinator is the stress test for this, it does an anim queue complete
				// wait on a trivial kanim... Clay please!
				if (anim != null && mode == KAnim.PlayMode.Paused && anim.numFrames > 0 &&
						__instance.currentFrame == 0 && inst != null && inst.GetAnimState(anim,
						KAnim.PlayMode.Loop) == mode)
					stopped = false;
			}
			__result = stopped;
			return false;
		}
	}

	/// <summary>
	/// Applied to KAnimControllerBase to make trivial anims stop triggering updates.
	/// </summary>
	[HarmonyPatch(typeof(KAnimControllerBase), "StartQueuedAnim")]
	public static class KAnimControllerBase_StartQueuedAnim_Patch {
		internal static bool Prepare() => FastTrackOptions.Instance.AnimOpts;

		/// <summary>
		/// Adjusts the play mode if the anim is trivial.
		/// </summary>
		internal static KAnim.PlayMode UpdateMode(KAnim.PlayMode mode,
				KAnimControllerBase controller) {
			var anim = controller.GetCurrentAnim();
			var inst = KAnimLoopOptimizer.Instance;
			if (anim != null && mode == KAnim.PlayMode.Loop && inst != null && controller.
					animQueue.Count == 0) {
				// Will set "paused" only if the anim is so short as to be unnoticeable
				mode = inst.GetAnimState(anim, mode);
				if (mode == KAnim.PlayMode.Paused) {
					// Trigger an instant stop
					controller.Stop();
					controller.currentFrame = 0;
					controller.SetElapsedTime(0.0f);
					PathPatches.DeferredTriggers.TriggerAndQueue(controller,
						(int)GameHashes.AnimQueueComplete, null);
				}
			}
			return mode;
		}

		/// <summary>
		/// Transpiles StartQueuedAnim to update the mode to Once on trivial anims.
		/// </summary>
		internal static TranspiledMethod Transpiler(TranspiledMethod instructions) {
			var target = typeof(KAnimControllerBase).GetFieldSafe(nameof(KAnimControllerBase.
				mode), false);
			var injection = typeof(KAnimControllerBase_StartQueuedAnim_Patch).GetMethodSafe(
				nameof(UpdateMode), true, typeof(KAnim.PlayMode), typeof(KAnimControllerBase));
			if (target == null || injection == null) {
				PUtil.LogWarning("Unable to patch KAnimControllerBase.StartQueuedAnim");
				foreach (var instr in instructions)
					yield return instr;
			} else
				foreach (var instr in instructions) {
					if (instr.Is(OpCodes.Stfld, target)) {
						yield return new CodeInstruction(OpCodes.Ldarg_0);
						yield return new CodeInstruction(OpCodes.Call, injection);
#if DEBUG
						PUtil.LogDebug("Patched KAnimControllerBase.StartQueuedAnim");
#endif
					}
					yield return instr;
				}
		}
	}

	/// <summary>
	/// Applied to KAnimControllerBase to only update the hidden flag if the visibility
	/// actually changed (yes the Klei method has a typo, like many...)
	/// </summary>
	[HarmonyPatch(typeof(KAnimControllerBase), nameof(KAnimControllerBase.SetSymbolVisiblity))]
	public static class KAnimControllerBase_SetSymbolVisiblity_Patch {
		internal static bool Prepare() => FastTrackOptions.Instance.AnimOpts;

		/// <summary>
		/// Applied before SetSymbolVisiblity runs.
		/// </summary>
		internal static bool Prefix(KAnimControllerBase __instance, KAnimHashedString symbol,
				bool is_visible) {
			bool changed = false;
			var hidden = __instance.hiddenSymbols;
			if (is_visible)
				changed = hidden.Remove(symbol);
			else if (!hidden.Contains(symbol)) {
				hidden.Add(symbol);
				// This is not called all that often and the hidden symbol list is usually
				// quite small, so not worth changing to hash set
				changed = true;
			}
			if (changed && __instance.curBuild != null)
				__instance.UpdateHidden();
			return false;
		}
	}

	/// <summary>
	/// Applied to KAnimSynchronizedController to not dirty rocket anims (among others)
	/// every frame.
	/// </summary>
	[HarmonyPatch(typeof(KAnimSynchronizedController), nameof(KAnimSynchronizedController.
		Dirty))]
	public static class KAnimSynchronizedController_Dirty_Patch {
		internal static bool Prepare() => FastTrackOptions.Instance.AnimOpts;

		/// <summary>
		/// Applied before Dirty runs.
		/// </summary>
		internal static bool Prefix(KAnimSynchronizedController __instance) {
			var syncController = __instance.synchronizedController;
			var controller = __instance.controller;
			if (syncController != null && controller != null) {
				// These setters unconditionally dirty and deregister/register the anim
				if (syncController.Offset != controller.Offset)
					syncController.Offset = controller.Offset;
				if (syncController.Pivot != controller.Pivot)
					syncController.Pivot = controller.Pivot;
				if (!Mathf.Approximately(syncController.Rotation, controller.Rotation))
					syncController.Rotation = controller.Rotation;
				if (syncController.FlipX != controller.FlipX)
					syncController.FlipX = controller.FlipX;
				if (syncController.FlipY != controller.FlipY)
					syncController.FlipY = controller.FlipY;
			}
			return false;
		}
	}

	/// <summary>
	/// Applied to KBatchedAnimController to remove some shockingly expensive asserts.
	/// </summary>
	[HarmonyPatch(typeof(KBatchedAnimController), nameof(KBatchedAnimController.SetBatchGroup))]
	public static class KBatchedAnimController_SetBatchGroup_Patch {
		internal static bool Prepare() => FastTrackOptions.Instance.AnimOpts;

		/// <summary>
		/// Applied before SetBatchGroup runs.
		/// </summary>
		internal static bool Prefix(KBatchedAnimController __instance, KAnimFileData kafd) {
			var id = __instance.batchGroupID;
			if (id.IsValid)
				PUtil.LogWarning("Batch group should only be set once!");
			else if (kafd == null)
				PUtil.LogWarning("No anim data for {0}!".F(__instance.name));
			else if (id != kafd.batchTag) {
				var bild = kafd.build;
				var inst = KAnimBatchManager.Instance();
				if (bild == null)
					PUtil.LogWarning("No build for anim {0} on {1}!".F(kafd.name, __instance.
						name));
				else if (!(id = bild.batchTag).IsValid || id == KAnimBatchManager.NO_BATCH)
					PUtil.LogWarning("Batch is not ready: " + __instance.name);
				else if (inst != null) {
					var bgd = KAnimBatchManager.instance.GetBatchGroupData(id);
					id = bild.batchTag;
					__instance.curBuild = bild;
					__instance.batchGroupID = id;
					__instance.symbolInstanceGpuData = new SymbolInstanceGpuData(bgd.
						maxSymbolsPerBuild);
					__instance.symbolOverrideInfoGpuData = new SymbolOverrideInfoGpuData(bgd.
						symbolFrameInstances.Count);
				}
			}
			return false;
		}
	}

	/// <summary>
	/// Applied to KBatchedAnimController to update much less in anims that are off screen.
	/// </summary>
	[HarmonyPatch(typeof(KBatchedAnimController), nameof(KBatchedAnimController.UpdateAnim))]
	public static class KBatchedAnimController_UpdateAnim_Patch {
		internal static bool Prepare() => FastTrackOptions.Instance.AnimOpts;

		/// <summary>
		/// Applied before UpdateAnim runs.
		/// </summary>
		internal static bool Prefix(KBatchedAnimController __instance, float dt) {
			if (__instance.IsActive())
				UpdateActive(__instance, dt);
			return false;
		}

		/// <summary>
		/// Updates an active batched anim controller.
		/// </summary>
		/// <param name="instance">The controller to update.</param>
		/// <param name="dt">The time since the last update.</param>
		private static void UpdateActive(KBatchedAnimController instance, float dt) {
			Transform transform;
			var batch = instance.batch;
			var visType = instance.visibilityType;
			bool visible = instance.IsVisible();
			// Check if moved, do this even if offscreen as it may move the anim on screen
			if (batch != null && (transform = instance.transform).hasChanged) {
				var lastChunk = instance.lastChunkXY;
				Vector3 pos = transform.position, posWithOffset = pos + instance.Offset;
				float z = pos.z;
				bool always = visType == KAnimControllerBase.VisibilityType.Always;
				transform.hasChanged = false;
				// If this is the only anim in the batch, and the Z coordinate changed,
				// override the Z in the batch
				if (batch.group.maxGroupSize == 1 && !Mathf.Approximately(instance.lastPos.z,
						z))
					batch.OverrideZ(z);
				instance.lastPos = posWithOffset;
				// This is basically GetCellXY() with less accesses to __instance.transform
				var cellCoords = Grid.CellSizeInMeters == 0.0f ? new Vector2I(
					(int)posWithOffset.x, (int)posWithOffset.y) : Grid.PosToXY(posWithOffset);
				if (!always && lastChunk != KBatchedAnimUpdater.INVALID_CHUNK_ID &&
						KAnimBatchManager.CellXYToChunkXY(cellCoords) != lastChunk) {
					// Re-register in a different batch
					instance.DeRegister();
					instance.Register();
				} else if (visible || always)
					// Only set dirty if it is on-screen now - changing visible sets dirty
					// If it moved into a different chunk, Register sets dirty
					instance.SetDirty();
			}
			// If it has a batch, and is active
			if (instance.batchGroupID != KAnimBatchManager.NO_BATCH) {
				var anim = instance.curAnim;
				var mode = instance.mode;
				bool force = instance.forceRebuild, stopped = instance.stopped;
				float t = instance.elapsedTime, increment = dt * instance.playSpeed;
				// Suspend updates if: not currently suspended, not force update, and one
				// of (paused, stopped, no anim, one time and finished with no more to play)
				if (!instance.suspendUpdates && !force && (mode == KAnim.PlayMode.Paused ||
						stopped || anim == null || (mode == KAnim.PlayMode.Once && (t > anim.
						totalTime || anim.totalTime <= 0f) && instance.animQueue.Count == 0)))
					instance.SuspendUpdates(true);
				if (visible || force) {
					var aem = instance.aem;
					var handle = instance.eventManagerHandle;
					instance.curAnimFrameIdx = instance.GetFrameIdx(t, true);
					// Trigger anim event manager if time advanced more than 0.01s
					if (handle.IsValid() && aem != null) {
						float elapsedTime = aem.GetElapsedTime(handle);
						if (Math.Abs(t - elapsedTime) > 0.01f)
							aem.SetElapsedTime(handle, t);
					}
					instance.UpdateFrame(t);
					// Time can be mutated by UpdateFrame
					if (!stopped && mode != KAnim.PlayMode.Paused)
						instance.SetElapsedTime(instance.elapsedTime + increment);
					instance.forceRebuild = false;
				} else if (visType == KAnimControllerBase.VisibilityType.OffscreenUpdate &&
						!stopped && mode != KAnim.PlayMode.Paused)
					// If invisible, only advance if offscreen update is enabled
					instance.SetElapsedTime(t + increment);
			}
		}
	}
}
