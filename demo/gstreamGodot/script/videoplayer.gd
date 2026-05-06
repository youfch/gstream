extends Node3D

## gStream Videoplayer Demo — 视频播放器控制场景
## 验证：Videoplayer 自定义二进制输入协议（键盘/鼠标/触摸/手柄/按钮点击）

# --- 状态变量 ---
var _click_count := 0
var _touch_count := 0
var _key_log := ""

# --- 节点引用 ---
@onready var status_label: Label = get_node_or_null("UI/Panel/VBox/StatusLabel")
@onready var click_count_label: Label = get_node_or_null("UI/Panel/VBox/ClickCountLabel")
@onready var touch_count_label: Label = get_node_or_null("UI/Panel/VBox/TouchCountLabel")
@onready var key_log_label: Label = get_node_or_null("UI/Panel/VBox/KeyLogLabel")
@onready var color_rect: ColorRect = get_node_or_null("UI/ColorRect")
@onready var video_player: VideoStreamPlayer = get_node_or_null("VideoPlayer")
@onready var video_status_label: Label = get_node_or_null("UI/Panel/VBox/VideoStatusLabel")
@onready var audio_player: AudioStreamPlayer = get_node_or_null("AudioPlayer")

# ============================================================
#  生命周期
# ============================================================

func _ready() -> void:
	_update_status("等待连接...")
	_update_video_status("未加载")
	connect_stream_server.call_deferred()

func _process(_delta: float) -> void:
	if video_player and video_player.is_playing():
		_update_video_status("播放中 %.1fs" % video_player.stream_position)
	elif video_player and video_player.stream_position > 0.0:
		_update_video_status("已暂停 %.1fs" % video_player.stream_position)

# ============================================================
#  输入事件
# ============================================================

func _unhandled_input(event: InputEvent) -> void:
	# --- 鼠标左键点击 ---
	if event is InputEventMouseButton and event.button_index == MOUSE_BUTTON_LEFT:
		if event.pressed:
			_click_count += 1
			if click_count_label:
				click_count_label.text = "鼠标点击: %d" % _click_count
			_flash_color(Color.CYAN)

	# --- 触摸 ---
	if event is InputEventScreenTouch:
		if event.pressed:
			_touch_count += 1
			if touch_count_label:
				touch_count_label.text = "触摸点击: %d" % _touch_count
			_flash_color(Color.MAGENTA)

	# --- 键盘（日志） ---
	if event is InputEventKey and event.pressed and not event.echo:
		var key_name = keycode_to_string(event.keycode)
		_key_log = key_name + " | " + _key_log
		if _key_log.length() > 60:
			_key_log = _key_log.substr(0, 60)
		if key_log_label:
			key_log_label.text = "键盘: %s" % _key_log
		_flash_color(Color.GREEN_YELLOW)

# ============================================================
#  UI 辅助
# ============================================================

func _flash_color(color: Color) -> void:
	if color_rect:
		color_rect.color = color
		var tween = create_tween()
		tween.tween_property(color_rect, "color", Color.TRANSPARENT, 0.4)

func _update_status(text: String) -> void:
	if status_label:
		status_label.text = "状态: " + text

func _update_video_status(text: String) -> void:
	if video_status_label:
		video_status_label.text = "视频: " + text

# ============================================================
#  信令连接
# ============================================================

func connect_stream_server() -> void:
	var ss = get_node_or_null("StreamServer")
	if ss and ss.has_signal("ClientConnected"):
		ss.connect("ClientConnected", _on_client_connected)
		ss.connect("ClientDisconnected", _on_client_disconnected)
		_update_status("就绪，等待连接...")
	elif ss:
		connect_stream_server.call_deferred()

func _on_client_connected(connection_id: String) -> void:
	_update_status("已连接 ✓  %s" % connection_id)
	_flash_color(Color.CYAN)

func _on_client_disconnected(connection_id: String) -> void:
	_update_status("已断开  %s" % connection_id)
	_flash_color(Color.ORANGE)

# ============================================================
#  UI 按钮回调
# ============================================================

func _on_play_pressed() -> void:
	if not video_player:
		return
	if not video_player.stream:
		var video_stream = load("res://video/sample.mp4")
		if video_stream:
			video_player.stream = video_stream
		else:
			_update_video_status("视频文件未找到")
			push_warning("无法加载视频文件: res://video/sample.mp4")
			return
	video_player.play()
	_update_video_status("播放中...")
	_flash_color(Color.GREEN)

func _on_pause_pressed() -> void:
	if not video_player:
		return
	if video_player.is_playing():
		video_player.paused = true
		_update_video_status("已暂停")
		_flash_color(Color.YELLOW)
	elif video_player.paused:
		video_player.paused = false
		_update_video_status("播放中...")
		_flash_color(Color.GREEN)

func _on_stop_pressed() -> void:
	if not video_player:
		return
	video_player.stop()
	_update_video_status("已停止")
	_flash_color(Color.RED)

func _on_reset_pressed() -> void:
	_click_count = 0
	_touch_count = 0
	_key_log = ""
	if click_count_label: click_count_label.text = "鼠标点击: 0"
	if touch_count_label: touch_count_label.text = "触摸点击: 0"
	if key_log_label: key_log_label.text = "键盘: "
	if video_player:
		video_player.stop()
	_update_video_status("未加载")
	_flash_color(Color.WHITE)

func _on_audio_pressed() -> void:
	if audio_player:
		if audio_player.playing:
			audio_player.stop()
		else:
			var stream = load("res://audio/Calm Inspiring Piano Logo.mp3")
			if stream:
				audio_player.stream = stream
				audio_player.play()
				_flash_color(Color.MEDIUM_PURPLE)
			else:
				push_warning("无法加载音频文件")

# ============================================================
#  工具
# ============================================================

static func keycode_to_string(code: Key) -> String:
	match code:
		KEY_SPACE: return "Space"
		KEY_ENTER: return "Enter"
		KEY_ESCAPE: return "Esc"
		KEY_TAB: return "Tab"
		KEY_BACKSPACE: return "Bksp"
		KEY_SHIFT: return "Shift"
		KEY_CTRL: return "Ctrl"
		KEY_ALT: return "Alt"
		KEY_LEFT: return "←"
		KEY_RIGHT: return "→"
		KEY_UP: return "↑"
		KEY_DOWN: return "↓"
		_:
			var ch = char(code)
			if ch.is_valid_identifier():
				return ch.to_upper()
			return "<%d>" % code
