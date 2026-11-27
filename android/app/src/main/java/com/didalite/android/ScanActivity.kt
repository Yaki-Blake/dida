package com.didalite.android

import android.Manifest
import android.app.Activity
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.Color
import android.graphics.ImageFormat
import android.hardware.Camera
import android.os.Bundle
import android.view.Gravity
import android.view.Surface
import android.view.SurfaceHolder
import android.view.SurfaceView
import android.view.ViewGroup
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.TextView
import android.widget.Toast

/**
 * 内置扫码页：只做一件事——把摄像头里的二维码解成字符串，原样回传。
 *
 * 用 android.hardware.Camera + zxing-core 逐帧解 NV21，不引 androidx / 相机X，
 * 也不依赖手机另装扫码 App。解到就把 text 放进 "SCAN_RESULT" 返回，
 * 由 SecondClassActivity 按官方二维码的格式解析、确认、提交。
 */
class ScanActivity : Activity() {

    private var camera: Camera? = null
    private var surface: SurfaceView? = null
    private var previewWidth = 0
    private var previewHeight = 0
    private var found = false

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        applyDidaTheme()

        val root = FrameLayout(this).apply { setBackgroundColor(Ui.PAPER) }
        surface = SurfaceView(this).also { sv ->
            root.addView(sv, FrameLayout.LayoutParams(MATCH_PARENT, MATCH_PARENT))
        }

        // 中间的取景框：只给个视觉参照，不参与解码（整帧都拿来解）
        val frame = ViewFrame(this)
        root.addView(
            frame,
            FrameLayout.LayoutParams(dpx(260), dpx(260)).apply {
                gravity = Gravity.CENTER
            },
        )

        val bar = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            setBackgroundColor(Ui.PAPER)
            setPadding(dpx(16), dpx(8), dpx(16), dpx(8))
        }
        val back = TextView(this).apply {
            text = "‹ 返回"
            textSize = 16f
            typeface = Ui.medium
            setTextColor(Ui.ACCENT_DEEP)
            setPadding(dpx(4), dpx(8), dpx(16), dpx(8))
            setOnClickListener { finish() }
        }
        bar.addView(back)
        bar.addView(TextView(this).apply {
            text = "扫码签到签退"
            textSize = 15f
            typeface = Ui.medium
            setTextColor(Ui.INK)
        })
        root.addView(
            bar,
            FrameLayout.LayoutParams(MATCH_PARENT, dpx(52)).apply { gravity = Gravity.TOP },
        )
        // 提示浮在黑色相机预览上，得用浅字 + 半透明底才看得清
        root.addView(
            TextView(this).apply {
                text = "把活动二维码放进框里"
                textSize = 12f
                typeface = Ui.body
                setTextColor(Ui.ACCENT_INK)
                gravity = Gravity.CENTER
                background = android.graphics.drawable.GradientDrawable().apply {
                    cornerRadius = dpx(20).toFloat()
                    setColor(Color.parseColor("#99000000"))
                }
                setPadding(dpx(16), dpx(8), dpx(16), dpx(8))
            },
            FrameLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply {
                gravity = Gravity.BOTTOM or Gravity.CENTER_HORIZONTAL
                bottomMargin = dpx(90)
            },
        )

        setContentView(root)

        if (checkSelfPermission(Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED) {
            openCamera()
        } else {
            requestPermissions(arrayOf(Manifest.permission.CAMERA), REQ_CAMERA)
        }
    }

    override fun onRequestPermissionsResult(
        requestCode: Int,
        permissions: Array<out String>,
        grantResults: IntArray,
    ) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        if (requestCode != REQ_CAMERA) return
        if (grantResults.firstOrNull() == PackageManager.PERMISSION_GRANTED) {
            openCamera()
        } else {
            toast("需要相机权限才能扫码，改用口令签到")
            finish()
        }
    }

    private fun openCamera() {
        try {
            camera = Camera.open(0)
        } catch (e: Exception) {
            toast("打不开摄像头，改用口令签到")
            finish()
            return
        }
        val cam = camera ?: return finish()
        val params = cam.parameters
        // 挑一个够用又不太大的分辨率：解码 480p 足够，太大反而慢。
        val sizes = params.supportedPreviewSizes
        val chosen = sizes.filter { it.width in 480..1280 }
            .minByOrNull { it.width * it.height }
            ?: sizes.firstOrNull()
        if (chosen != null) {
            previewWidth = chosen.width
            previewHeight = chosen.height
            params.setPreviewSize(chosen.width, chosen.height)
        }
        params.previewFormat = ImageFormat.NV21
        cam.parameters = params
        // 关键：摄像头传感器是横的，不设这个角画面会横躺 90°。
        setCameraDisplayOrientation()
        surface?.holder?.addCallback(object : SurfaceHolder.Callback {
            override fun surfaceCreated(holder: SurfaceHolder) {
                startPreview(holder)
            }

            override fun surfaceChanged(holder: SurfaceHolder, format: Int, width: Int, height: Int) {
                runCatching { cam.stopPreview() }
                startPreview(holder)
            }

            override fun surfaceDestroyed(holder: SurfaceHolder) {
                runCatching { cam.setPreviewCallback(null); cam.stopPreview() }
            }
        })
    }

    private fun startPreview(holder: SurfaceHolder) {
        val cam = camera ?: return
        runCatching {
            fitSurface()
            cam.setPreviewDisplay(holder)
            cam.setPreviewCallback { data, _ -> decode(data) }
            cam.startPreview()
        }
    }

    /**
     * 摄像头传感器输出的是横屏帧，setDisplayOrientation 转 90° 后显示出来是竖屏，
     * 于是显示宽高比 = previewHeight : previewWidth。SurfaceView 若 MATCH_PARENT
     * 会把横屏帧硬拉成竖屏、人也被压扁——这里按显示比例居中等比缩放（cover 填满），
     * 只改显示，解码仍吃原始 NV21 帧（QR 码旋转不敏感，不受影响）。
     */
    private fun fitSurface() {
        val sv = surface ?: return
        if (previewWidth <= 0 || previewHeight <= 0) return
        val displayW = previewHeight
        val displayH = previewWidth
        val screenW = resources.displayMetrics.widthPixels
        val screenH = resources.displayMetrics.heightPixels
        val scale = maxOf(screenW.toFloat() / displayW, screenH.toFloat() / displayH)
        val w = (displayW * scale).toInt()
        val h = (displayH * scale).toInt()
        // 尺寸没变就别重设，否则会反复触发 surfaceChanged 形成循环
        val cur = sv.layoutParams
        if (cur.width == w && cur.height == h) return
        sv.layoutParams = FrameLayout.LayoutParams(w, h).apply { gravity = Gravity.CENTER }
    }

    /** 让预览画面跟着手机自然方向站正；不设的话竖屏打开摄像头画面会横躺 90°。 */
    private fun setCameraDisplayOrientation() {
        val cam = camera ?: return
        val rotation = when (windowManager.defaultDisplay.rotation) {
            Surface.ROTATION_90 -> 90
            Surface.ROTATION_180 -> 180
            Surface.ROTATION_270 -> 270
            else -> 0
        }
        val info = Camera.CameraInfo()
        Camera.getCameraInfo(0, info)
        val degrees = when (info.facing) {
            Camera.CameraInfo.CAMERA_FACING_FRONT ->
                (360 - (info.orientation + rotation) % 360) % 360
            else -> (info.orientation - rotation + 360) % 360
        }
        cam.setDisplayOrientation(degrees)
    }

    /** 每帧尝试解一次；解到就直接回传并结束，不再处理后续帧。 */
    private fun decode(data: ByteArray) {
        if (found || previewWidth <= 0 || previewHeight <= 0) return
        val text = ScanDecoder.decodeNv21(data, previewWidth, previewHeight) ?: return
        found = true
        val intent = Intent().putExtra("SCAN_RESULT", text)
        setResult(RESULT_OK, intent)
        finish()
    }

    override fun onPause() {
        runCatching { camera?.setPreviewCallback(null); camera?.stopPreview() }
        super.onPause()
    }

    override fun onResume() {
        super.onResume()
        if (camera != null && surface?.holder?.surface != null) {
            startPreview(surface!!.holder)
        }
    }

    override fun onDestroy() {
        runCatching {
            camera?.setPreviewCallback(null)
            camera?.stopPreview()
            camera?.release()
        }
        camera = null
        super.onDestroy()
    }

    /** 一个圆角描边的取景框，纯装饰。 */
    private class ViewFrame(context: Activity) : android.view.View(context) {
        // dpx 是 Activity 的扩展，这里只是个 View，自己按 density 换算。
        private fun dp(value: Int) = (value * resources.displayMetrics.density).toInt().toFloat()

        override fun onDraw(canvas: android.graphics.Canvas) {
            val w = width.toFloat()
            val h = height.toFloat()
            val r = dp(14)
            val paint = android.graphics.Paint(android.graphics.Paint.ANTI_ALIAS_FLAG).apply {
                color = Ui.ACCENT
                style = android.graphics.Paint.Style.STROKE
                strokeWidth = dp(2)
            }
            canvas.drawRoundRect(
                android.graphics.RectF(0f, 0f, w, h), r, r, paint,
            )
            // 四角加粗一小段，像个取景器
            paint.strokeWidth = dp(4)
            val len = dp(20)
            val t = dp(4)
            listOf(
                android.graphics.RectF(0f, 0f, len, t),
                android.graphics.RectF(0f, 0f, t, len),
                android.graphics.RectF(w - len, 0f, w, t),
                android.graphics.RectF(w - t, 0f, w, len),
                android.graphics.RectF(0f, h - t, len, h),
                android.graphics.RectF(0f, h - len, t, h),
                android.graphics.RectF(w - len, h - t, w, h),
                android.graphics.RectF(w - t, h - len, w, h),
            ).forEach { canvas.drawRect(it, paint) }
        }
    }

    private fun toast(text: String) = Toast.makeText(this, text, Toast.LENGTH_SHORT).show()

    private companion object {
        const val REQ_CAMERA = 2001
        const val MATCH_PARENT = ViewGroup.LayoutParams.MATCH_PARENT
    }
}
