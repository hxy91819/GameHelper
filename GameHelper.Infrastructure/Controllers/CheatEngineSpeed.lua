openProcess(targetPid)
-- Keep a duplicate of this process object: PID reuse and a GameHelper restart
-- must not change which game's exit owns this CE instance.
local openedHandle = getOpenedProcessHandle()
local cePid = getCheatEngineProcessID()
local watchHandle = duplicateHandle(openedHandle, cePid, cePid)
if watchHandle and watchHandle ~= 0 then
  local exitTimer = createTimer(getMainForm(), false)
  exitTimer.Interval = 500
  local function stopWatching()
    exitTimer.Enabled = false
    exitTimer.destroy()
    executeCodeLocalEx('kernel32.CloseHandle', watchHandle)
  end
  exitTimer.OnTimer = function()
    -- A user taking this CE instance over for another target cancels ownership.
    if getOpenedProcessID() ~= targetPid or getOpenedProcessHandle() ~= openedHandle then
      stopWatching()
      return
    end
    local state = executeCodeLocalEx('kernel32.WaitForSingleObject', watchHandle, 0)
    if state == 0 then
      stopWatching()
      closeCE()
    elseif state ~= 258 then -- WAIT_TIMEOUT is the only still-running result.
      stopWatching()
      print('GameHelper: stopped exit watcher because the process wait failed')
    end
  end
  exitTimer.Enabled = true
else
  print('GameHelper: could not watch the game process for automatic CE cleanup')
end
speedhack_setSpeed(targetSpeed)
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
    mono_invoke_method(nil, setter, nil, {mode == 1 and 1 or targetSpeed})
  end
end
