//! AMD BLAS through the maintained rocmrc crate. No handwritten C declarations.
use super::{Buffer, Library};
use crate::Result;
use rocmrc::rocblas::{result, sys};
use std::path::Path;

pub(super) struct Blas {
    handle: sys::rocblas_handle,
    _library: Library,
}
impl Blas {
    /// FP16 operands use the GPU matrix units; accumulation and output are FP32.
    pub fn matmul_half(
        &self,
        w: &Buffer,
        x: &Buffer,
        y: &Buffer,
        rows: usize,
        cols: usize,
        batch: usize,
    ) -> Result<()> {
        use sys::{rocblas_datatype as Data, rocblas_operation as Op};
        let alpha = 1.0f32;
        let beta = 0.0f32;
        unsafe {
            result::gemm_ex(
                self.handle,
                Op::rocblas_operation_transpose,
                Op::rocblas_operation_none,
                i32::try_from(rows)?,
                i32::try_from(batch)?,
                i32::try_from(cols)?,
                (&alpha as *const f32).cast(),
                w.pointer,
                Data::rocblas_datatype_f16_r,
                i32::try_from(cols)?,
                x.pointer,
                Data::rocblas_datatype_f16_r,
                i32::try_from(cols)?,
                (&beta as *const f32).cast(),
                y.pointer,
                Data::rocblas_datatype_f32_r,
                i32::try_from(rows)?,
                y.pointer,
                Data::rocblas_datatype_f32_r,
                i32::try_from(rows)?,
                Data::rocblas_datatype_f32_r,
                sys::rocblas_gemm_algo::rocblas_gemm_algo_standard,
                0,
                0,
            )?;
        }
        Ok(())
    }
    pub fn matmul(
        &self,
        w: &Buffer,
        x: &Buffer,
        y: &Buffer,
        rows: usize,
        cols: usize,
        batch: usize,
    ) -> Result<()> {
        let alpha = 1.0;
        let beta = 0.0;
        unsafe {
            result::sgemm(
                self.handle,
                sys::rocblas_operation::rocblas_operation_transpose,
                sys::rocblas_operation::rocblas_operation_none,
                i32::try_from(rows)?,
                i32::try_from(batch)?,
                i32::try_from(cols)?,
                &alpha,
                w.pointer.cast(),
                i32::try_from(cols)?,
                x.pointer.cast(),
                i32::try_from(cols)?,
                &beta,
                y.pointer.cast(),
                i32::try_from(rows)?,
            )?;
        }
        Ok(())
    }
    pub fn open(directory: &Path) -> Result<Self> {
        let library = Library::open(&directory.join("rocblas.dll"))?;
        let handle = result::create_handle()?;
        Ok(Self {
            handle,
            _library: library,
        })
    }
    pub fn matvec(
        &self,
        w: &Buffer,
        x: &Buffer,
        y: &Buffer,
        rows: usize,
        cols: usize,
    ) -> Result<()> {
        let alpha = 1.0;
        let beta = 0.0;
        // Row-major [rows, cols] is column-major [cols, rows], transposed.
        unsafe {
            result::sgemv(
                self.handle,
                sys::rocblas_operation::rocblas_operation_transpose,
                i32::try_from(cols)?,
                i32::try_from(rows)?,
                &alpha,
                w.pointer.cast(),
                i32::try_from(cols)?,
                x.pointer.cast(),
                1,
                &beta,
                y.pointer.cast(),
                1,
            )?;
        }
        Ok(())
    }
}
impl Drop for Blas {
    fn drop(&mut self) {
        let _ = unsafe { result::destroy_handle(self.handle) };
    }
}
