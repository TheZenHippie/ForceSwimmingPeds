---
name: gtav-user-defined-area
description: >-
  Use this skill when developing GTA V ScriptHookVDotNet3 scripts that require creating
  or modifying user-defined 3D areas, interactive crosshair/camera location pickers,
  visual markers/boundary cylinders, LemonUI coordinate sliders, and area entity state machines.
---

# GTA V: Interactive User-Defined Areas, Visual Aids & Free Camera/Cursor Selection

This skill provides the architectural patterns, math, native calls, and LemonUI integration required to let players interactively define and adjust a 3D world area (pools, lakes, zones, spawner areas) with visual aids and coordinate controls.

---

## 1. Interactive 3D World Raycasting

### Screen-to-World Projection (`ScreenRelToWorld`)
Converts 2D normalized screen coordinates $(X, Y \in [0.0, 1.0])$ into a 3D world ray origin and direction vector using the camera's rotation matrix:

```csharp
public static Vector3 ScreenRelToWorld(Vector3 camPos, Vector3 camRot, Vector2 screenCoord)
{
    Vector3 camForward = RotationToDirection(camRot);
    Vector3 rotUp = camRot + new Vector3(10.0f, 0.0f, 0.0f);
    Vector3 rotDown = camRot + new Vector3(-10.0f, 0.0f, 0.0f);
    Vector3 rotLeft = camRot + new Vector3(0.0f, 0.0f, -10.0f);
    Vector3 rotRight = camRot + new Vector3(0.0f, 0.0f, 10.0f);

    Vector3 camRight = RotationToDirection(rotRight) - RotationToDirection(rotLeft);
    Vector3 camUp = RotationToDirection(rotUp) - RotationToDirection(rotDown);

    float rollRad = -(float)(camRot.Y * (Math.PI / 180.0));
    Vector3 camRightRoll = (camRight * (float)Math.Cos(rollRad)) - (camUp * (float)Math.Sin(rollRad));
    Vector3 camUpRoll = (camRight * (float)Math.Sin(rollRad)) + (camUp * (float)Math.Cos(rollRad));

    Vector3 point3D = camPos + (camForward * 10.0f);
    Vector3 point3DTest = point3D + camRightRoll + camUpRoll;

    if (!WorldToScreen(point3DTest, out Vector2 screenPointTest) || !WorldToScreen(point3D, out Vector2 screenPoint))
    {
        return camPos + (camForward * 10.0f);
    }

    if (Math.Abs(screenPointTest.X - screenPoint.X) < 0.0001f || Math.Abs(screenPointTest.Y - screenPoint.Y) < 0.0001f)
    {
        return camPos + (camForward * 10.0f);
    }

    float scaleX = (screenCoord.X - screenPoint.X) / (screenPointTest.X - screenPoint.X);
    float scaleY = (screenCoord.Y - screenPoint.Y) / (screenPointTest.Y - screenPoint.Y);

    return point3D + (camRightRoll * scaleX) + (camUpRoll * scaleY);
}

public static bool WorldToScreen(Vector3 worldPos, out Vector2 screenPos)
{
    var outX = new OutputArgument();
    var outY = new OutputArgument();
    bool success = Function.Call<bool>(Hash.GET_SCREEN_COORD_FROM_WORLD_COORD, worldPos.X, worldPos.Y, worldPos.Z, outX, outY);
    screenPos = new Vector2(outX.GetResult<float>(), outY.GetResult<float>());
    return success;
}

public static Vector3 RotationToDirection(Vector3 rot)
{
    float z = (float)(rot.Z * (Math.PI / 180.0));
    float x = (float)(rot.X * (Math.PI / 180.0));
    float num = (float)Math.Abs(Math.Cos(x));
    return new Vector3(-(float)(Math.Sin(z) * num), (float)(Math.Cos(z) * num), (float)Math.Sin(x));
}
```

### Shape Testing & Water Surface Snapping
Standard LOS probes hit pool bottoms and underwater terrain rather than the water surface. Always probe for water height to snap coordinates accurately:

```csharp
RaycastResult ray = World.Raycast(camPos, rayEnd, (IntersectFlags)(-1), Game.Player.Character);
Vector3 hitPoint = ray.DidHit ? ray.HitPosition : (camPos + (rayDir * 40.0f));

// Probe water height
var waterHeightArg = new OutputArgument();
if (Function.Call<bool>(Hash.GET_WATER_HEIGHT, hitPoint.X, hitPoint.Y, hitPoint.Z, waterHeightArg))
{
    float wh = waterHeightArg.GetResult<float>();
    if (wh > hitPoint.Z - 6.0f && wh < hitPoint.Z + 6.0f)
    {
        hitPoint = new Vector3(hitPoint.X, hitPoint.Y, wh);
    }
}
```

---

## 2. Free Cursor & Crosshair Targeting Controls

1. **Keep Cursor Active Every Frame**:
   ```csharp
   Function.Call(Hash.SET_MOUSE_CURSOR_THIS_FRAME);
   float curX = Function.Call<float>(Hash.GET_CONTROL_NORMAL, 0, (int)Control.CursorX);
   float curY = Function.Call<float>(Hash.GET_CONTROL_NORMAL, 0, (int)Control.CursorY);
   ```
2. **Suppress Disruptive Controls**:
   Prevent weapon firing, weapon wheel, or attack animations during picking mode:
   ```csharp
   Game.DisableControlThisFrame(Control.Attack);
   Game.DisableControlThisFrame(Control.Attack2);
   Game.DisableControlThisFrame(Control.Aim);
   Game.DisableControlThisFrame(Control.MeleeAttack1);
   Game.DisableControlThisFrame(Control.SelectWeapon);
   ```
3. **Draw 2D Crosshair Reticle**:
   ```csharp
   // Vertical Bar
   Function.Call(Hash.DRAW_RECT, screenX, screenY, 0.0020f, 0.038f, 255, 255, 255, 230);
   // Horizontal Bar
   Function.Call(Hash.DRAW_RECT, screenX, screenY, 0.022f, 0.0035f, 255, 255, 255, 230);
   // Focal Dot
   Function.Call(Hash.DRAW_RECT, screenX, screenY, 0.0045f, 0.0075f, 0, 200, 255, 255);
   ```
4. **Commit / Cancel Inputs**:
   - **Accept**: `Game.IsControlJustPressed(Control.Attack) || Game.IsControlJustPressed(Control.CursorAccept)`
   - **Cancel**: `Game.IsControlJustPressed(Control.Attack2) || Game.IsControlJustPressed(Control.CursorCancel) || Game.IsKeyPressed(Keys.Escape)`

---

## 3. Visual Aids (3D In-World Markers)

### Target Anchor Marker
Display a live placement cylinder and downward-pointing cone at the targeted 3D coordinate:
```csharp
World.DrawMarker(MarkerType.VerticalCylinder, hitPoint, Vector3.Zero, Vector3.Zero,
    new Vector3(0.8f, 0.8f, 1.0f), Color.FromArgb(180, 0, 220, 255));
World.DrawMarker(MarkerType.UpsideDownCone, hitPoint + new Vector3(0, 0, 1.4f), Vector3.Zero,
    new Vector3(0, 180, 0), new Vector3(0.5f, 0.5f, 0.7f), Color.FromArgb(220, 255, 215, 0));
```

### Area Boundary Cylinder
Display a translucent cylinder covering the exact radius and vertical bounds of the defined area:
```csharp
World.DrawMarker(MarkerType.VerticalCylinder, new Vector3(center.X, center.Y, center.Z - 0.2f),
    Vector3.Zero, Vector3.Zero, new Vector3(radius * 2.0f, radius * 2.0f, 1.5f), Color.FromArgb(60, 0, 160, 255));
```

---

## 4. LemonUI Dual-Input Sliders (Nudge + Direct Text Entry)

Provide both continuous slider adjustment (arrow keys) and direct numerical input (Enter key):

```csharp
_sliderX = new NativeSliderItem("X Coordinate: 0.00", "Use Left/Right to nudge (±0.5m). Press Enter to type exact value.", 200, 100);

// Nudge via Arrow Keys
_sliderX.ValueChanged += (sender, e) =>
{
    if (_isUpdatingUI) return;
    int diff = _sliderX.Value - _lastSliderX;
    if (diff != 0)
    {
        _targetLocation = new Vector3(_targetLocation.X + (diff * 0.5f), _targetLocation.Y, _targetLocation.Z);
        _lastSliderX = _sliderX.Value;
        _sliderX.Title = $"X Coordinate: {_targetLocation.X:F2}";
        if (_sliderX.Value <= 10 || _sliderX.Value >= 190) { /* Re-center slider to 100 */ }
    }
};

// Direct Typing via Enter Key
_sliderX.Activated += (sender, e) =>
{
    string input = Game.GetUserInput(WindowTitle.EnterMessage60, _targetLocation.X.ToString("F2", CultureInfo.InvariantCulture), 30);
    if (!string.IsNullOrEmpty(input) && float.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out float val))
    {
        _targetLocation = new Vector3(val, _targetLocation.Y, _targetLocation.Z);
        UpdateSliders();
        SaveSettings();
    }
};
```

---

## 5. Area State Machine & Clean Teardown

- **Find Candidates**: `World.GetNearbyPeds(center, radius)`
- **Immunities on Activation**:
  ```csharp
  Function.Call(Hash.CLEAR_PED_TASKS, ped.Handle);
  Function.Call(Hash.SET_BLOCKING_OF_NON_TEMPORARY_EVENTS, ped.Handle, true);
  Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, ped.Handle, 17, false); // BF_AlwaysFlee = false
  Function.Call(Hash.SET_PED_DIES_IN_WATER, ped.Handle, false);
  Function.Call(Hash.SET_PED_CONFIG_FLAG, ped.Handle, 64, true);         // DrownsInWater = false
  Function.Call(Hash.SET_PED_MAX_TIME_UNDERWATER, ped.Handle, 600.0f);
  ```
- **Controlled Exit on Disable**:
  When unchecking the behavior or disabling the area, do **not** abruptly delete tasks. Transition all tracked entities into an exit/return phase directing them back to their entry coordinates, and release flags only once dry land is reached.
