//! Opt-in GPU event timing. Normal inference does not allocate timing events.
use super::{Api, hip};
use crate::Result;
use std::{collections::BTreeMap, ptr, rc::Rc};

pub(super) struct Timer {
    api: Rc<Api>,
    start: hip::hipEvent_t,
    end: hip::hipEvent_t,
}
impl Timer {
    pub fn start(api: Rc<Api>) -> Result<Self> {
        let mut timer = Self {
            api,
            start: ptr::null_mut(),
            end: ptr::null_mut(),
        };
        timer.api.check(
            unsafe { hip::hipEventCreate(&mut timer.start) },
            "create timing event",
        )?;
        timer.api.check(
            unsafe { hip::hipEventCreate(&mut timer.end) },
            "create timing event",
        )?;
        timer.api.check(
            unsafe { hip::hipEventRecord(timer.start, ptr::null_mut()) },
            "record start",
        )?;
        Ok(timer)
    }
    pub fn stop(&self) -> Result<()> {
        self.api.check(
            unsafe { hip::hipEventRecord(self.end, ptr::null_mut()) },
            "record end",
        )
    }
    fn milliseconds(&self) -> Result<f64> {
        self.api.check(
            unsafe { hip::hipEventSynchronize(self.end) },
            "wait for timing",
        )?;
        let mut milliseconds = 0.0;
        self.api.check(
            unsafe { hip::hipEventElapsedTime(&mut milliseconds, self.start, self.end) },
            "read timing",
        )?;
        Ok(milliseconds as f64)
    }
}
impl Drop for Timer {
    fn drop(&mut self) {
        unsafe {
            if !self.start.is_null() {
                hip::hipEventDestroy(self.start);
            }
            if !self.end.is_null() {
                hip::hipEventDestroy(self.end);
            }
        }
    }
}

#[derive(Default)]
pub(super) struct Profile(pub Vec<(String, Timer)>);
impl Profile {
    pub fn summary(&self) -> Result<BTreeMap<String, (usize, f64)>> {
        let mut summary = BTreeMap::<String, (usize, f64)>::new();
        for (name, timer) in &self.0 {
            let entry = summary.entry(name.clone()).or_default();
            entry.0 += 1;
            entry.1 += timer.milliseconds()?;
        }
        Ok(summary)
    }
}
