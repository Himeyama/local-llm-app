//! Reuse scratch allocations on the single default HIP stream. A dropped
//! buffer's next use is queued after its previous users, so reuse needs no
//! hipFree synchronization. Persistent weights and caches keep normal ownership.
use super::{Api, Handle, hip};
use crate::Result;
use std::{
    cell::{Cell, RefCell},
    collections::BTreeMap,
    ptr,
    rc::Rc,
};

const MAX_CACHED_BYTES: usize = 128 * 1024 * 1024;
const MAX_CACHED_ALLOCATION: usize = 64 * 1024 * 1024;
const ALIGNMENT: usize = 4096;

pub(super) struct Pool {
    api: Rc<Api>,
    free: RefCell<BTreeMap<usize, Vec<Handle>>>,
    cached_bytes: Cell<usize>,
    allocations: Cell<u64>,
    reuses: Cell<u64>,
}
impl Pool {
    pub fn new(api: Rc<Api>) -> Self {
        Self {
            api,
            free: RefCell::new(BTreeMap::new()),
            cached_bytes: Cell::new(0),
            allocations: Cell::new(0),
            reuses: Cell::new(0),
        }
    }
    pub fn acquire(&self, bytes: usize) -> Result<(Handle, usize)> {
        // Attention scratch grows each batch. Exact-size bins retain hundreds
        // of obsolete sizes and eventually starve the frequently reused buffers.
        let alignment = if bytes <= 1024 * 1024 {
            bytes
                .checked_next_power_of_two()
                .ok_or("GPU allocation size overflow")?
                .max(ALIGNMENT)
        } else if bytes <= 4 * 1024 * 1024 {
            256 * 1024
        } else if bytes <= MAX_CACHED_ALLOCATION {
            1024 * 1024
        } else {
            ALIGNMENT
        };
        let capacity = bytes
            .checked_add(alignment - 1)
            .ok_or("GPU allocation size overflow")?
            & !(alignment - 1);
        // A growing Attention score buffer leaves slightly larger idle blocks.
        // Reuse the smallest adequate block rather than caching every shape.
        // Bound excess capacity so small persistent buffers cannot consume a
        // large scratch allocation when checkpoints are created.
        let reusable = self
            .free
            .borrow()
            .range(capacity..=capacity.saturating_mul(2))
            .find(|(_, pointers)| !pointers.is_empty())
            .map(|(&size, _)| size);
        if let Some(size) = reusable {
            let pointer = self
                .free
                .borrow_mut()
                .get_mut(&size)
                .unwrap()
                .pop()
                .unwrap();
            self.cached_bytes.set(self.cached_bytes.get() - size);
            self.reuses.set(self.reuses.get() + 1);
            return Ok((pointer, size));
        }
        let mut pointer = ptr::null_mut();
        self.api.check(
            unsafe { hip::hipMalloc(&mut pointer, capacity) },
            "hipMalloc",
        )?;
        self.allocations.set(self.allocations.get() + 1);
        Ok((pointer, capacity))
    }
    pub fn release(&self, pointer: Handle, capacity: usize) {
        if capacity <= MAX_CACHED_ALLOCATION {
            // Retain the newly used size instead of permanently locking the pool
            // to old shapes. Evict large idle allocations first under pressure.
            while self.cached_bytes.get() + capacity > MAX_CACHED_BYTES {
                let largest = self
                    .free
                    .borrow()
                    .iter()
                    .rev()
                    .find(|(_, v)| !v.is_empty())
                    .map(|(&size, _)| size);
                let Some(size) = largest else { break };
                let old = self
                    .free
                    .borrow_mut()
                    .get_mut(&size)
                    .unwrap()
                    .pop()
                    .unwrap();
                self.cached_bytes.set(self.cached_bytes.get() - size);
                unsafe {
                    hip::hipFree(old);
                }
            }
            self.free
                .borrow_mut()
                .entry(capacity)
                .or_default()
                .push(pointer);
            self.cached_bytes.set(self.cached_bytes.get() + capacity);
        } else {
            unsafe {
                hip::hipFree(pointer);
            }
        }
    }
    pub fn statistics(&self) -> (u64, u64, usize) {
        (
            self.allocations.get(),
            self.reuses.get(),
            self.cached_bytes.get(),
        )
    }
}
impl Drop for Pool {
    fn drop(&mut self) {
        for pointers in self.free.get_mut().values() {
            for &pointer in pointers {
                unsafe {
                    hip::hipFree(pointer);
                }
            }
        }
    }
}
