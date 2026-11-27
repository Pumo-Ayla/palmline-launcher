"""Run a gated prewarm worker and its compiler children in one Windows job."""
from contextlib import ExitStack
import ctypes
from ctypes import wintypes
import os
import subprocess
import _winapi


def run(command, cancel_event):
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    handle = wintypes.HANDLE
    kernel.OpenEventW.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.LPCWSTR]
    kernel.OpenEventW.restype = handle
    kernel.CreateJobObjectW.argtypes = [ctypes.c_void_p, wintypes.LPCWSTR]
    kernel.CreateJobObjectW.restype = handle
    kernel.AssignProcessToJobObject.argtypes = [handle, handle]
    kernel.TerminateJobObject.argtypes = [handle, wintypes.UINT]

    def checked(value):
        if not value:
            raise ctypes.WinError(ctypes.get_last_error())
        return value

    with ExitStack() as handles:
        # The launcher is the session's parent. Its process handle is also
        # signaled on an abnormal exit, when the UI cannot set its cancel event.
        synchronize = 0x00100000
        try:
            owner = _winapi.OpenProcess(synchronize, False, os.getppid())
        except OSError as exc:
            if exc.winerror == 87:  # the parent has already exited
                return None
            raise
        handles.callback(_winapi.CloseHandle, owner)
        cancel = kernel.OpenEventW(synchronize, False, cancel_event)
        if not cancel and _winapi.WaitForSingleObject(owner, 0) == 0:
            return None
        checked(cancel)
        handles.callback(_winapi.CloseHandle, cancel)
        if _winapi.WaitForMultipleObjects([cancel, owner], False, 0) != _winapi.WAIT_TIMEOUT:
            return None
        job = checked(kernel.CreateJobObjectW(None, None))
        handles.callback(_winapi.CloseHandle, job)
        with subprocess.Popen(command, stdin=subprocess.PIPE, creationflags=subprocess.CREATE_NO_WINDOW) as worker:
            if not kernel.AssignProcessToJobObject(job, int(worker._handle)):
                error = ctypes.WinError(ctypes.get_last_error())
                worker.stdin.close()  # EOF releases the unopened gate.
                raise error
            try:
                # No compiler can start before the worker and its descendants are contained.
                worker.stdin.write(b"PREPARE\n")
                worker.stdin.close()
                signaled = _winapi.WaitForMultipleObjects([cancel, owner, int(worker._handle)], False, _winapi.INFINITE)
                if signaled in (0, 1):
                    return None
                return worker.wait()
            finally:
                # Also collects any helpers left alive if the worker itself failed.
                checked(kernel.TerminateJobObject(job, 1))
