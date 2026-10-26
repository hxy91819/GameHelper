openProcess(targetPid)
local openedHandle = getOpenedProcessHandle()
local cePid = getCheatEngineProcessID()
local watchHandle = duplicateHandle(openedHandle, cePid, cePid)
local exitTimer
local hotkeys = {}
local stopped = false
local currentSpeed = targetSpeed

local function stopWatching()
  if stopped then return end
  stopped = true
  -- Each cleanup is independent so one failed destroy cannot leak other hotkeys.
  for _, hotkey in ipairs(hotkeys) do
    pcall(function() hotkey.destroy() end)
  end
  hotkeys = {}
  if exitTimer then
    pcall(function() exitTimer.Enabled = false end)
    pcall(function() exitTimer.destroy() end)
  end
  if watchHandle and watchHandle ~= 0 then
    pcall(function() executeCodeLocalEx('kernel32.CloseHandle', watchHandle) end)
  end
end

local function ownsTarget()
  return not stopped and getOpenedProcessID() == targetPid
    and getOpenedProcessHandle() == openedHandle
end

local function applySpeed(speed)
  speedhack_setSpeed(speed)
  currentSpeed = speed
  -- SpeedhackV3 reuses a game's existing Unity hook without restoring this CE
  -- instance's setter. Its requested speed changes, but the live timeScale can stay 1.
  if getAddressSafe('set_timeScaleEntryHook') ~= nil
      and UnityEngineTimeSetTimeScaleMethod == nil then
    assert(LaunchMonoDataCollector(), 'GameHelper: cannot reconnect to the Unity speed hook')
    local timeClass = mono_findClass('UnityEngine', 'Time')
    assert(timeClass and timeClass ~= 0, 'GameHelper: Unity Time class unavailable')
    local getter = mono_class_findMethod(timeClass, 'get_timeScale')
    local setter = mono_class_findMethod(timeClass, 'set_timeScale')
    assert(getter and getter ~= 0 and setter and setter ~= 0,
      'GameHelper: Unity timeScale methods unavailable')
    local currentScale = mono_invoke_method(nil, getter, nil, {})
    assert(type(currentScale) == 'number', 'GameHelper: Unity timeScale is unreadable')
    if currentScale > 0 then
      local mode = readInteger('speedhack_unityspeedhackmethod')
      assert(mode == 0 or mode == 1, 'GameHelper: unknown Unity speed hook mode')
      -- Multiplicative hooks already multiply by targetSpeed; passing targetSpeed
      -- again would produce 2.25 at 1.5x. Zero remains paused until the game resumes.
      mono_invoke_method(nil, setter, nil, {mode == 1 and 1 or speed})
    end
  end
end

if watchHandle and watchHandle ~= 0 then
  exitTimer = createTimer(getMainForm(), false)
  exitTimer.Interval = 500
  exitTimer.OnTimer = function()
    if not ownsTarget() then
      stopWatching()
      return
    end
    local state = executeCodeLocalEx('kernel32.WaitForSingleObject', watchHandle, 0)
    if state == 0 then
      stopWatching()
      closeCE()
    elseif state ~= 258 then
      stopWatching()
      print('GameHelper: stopped exit watcher because the process wait failed')
    end
  end
  exitTimer.Enabled = true
else
  print('GameHelper: could not watch the game process; speed hotkeys are disabled')
end

local ok, err = pcall(function()
  applySpeed(targetSpeed)
  if not watchHandle or watchHandle == 0 then return end
  for _, binding in ipairs(speedHotkeys) do
    local preset = binding.speed
    local hotkey = createHotkey(function()
      if not ownsTarget() then stopWatching(); return end
      local state = executeCodeLocalEx('kernel32.WaitForSingleObject', watchHandle, 0)
      if state == 0 then stopWatching(); closeCE(); return end
      if state ~= 258 then stopWatching(); return end
      local speed = preset
      if speed == nil then speed = currentSpeed == 1 and targetSpeed or 1 end
      local changed, failure = pcall(applySpeed, speed)
      if not changed then print('GameHelper: speed change failed: ' .. tostring(failure)) end
    end, table.unpack(binding.keys))
    assert(hotkey, 'GameHelper: could not register speed hotkey')
    hotkeys[#hotkeys + 1] = hotkey
    -- Limit repeated activations when the key is held.
    hotkey.DelayBetweenActivate = 250
  end
end)
if not ok then
  stopWatching()
  error(err)
end
