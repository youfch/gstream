extends Node3D

## gStream Receiver Demo (GDExtension) — 推流测试场景控制器
## 验证：WASD移动、Space跳跃、鼠标点击/拖拽、触摸

# --- 状态变量 ---
var _click_count := 0
var _touch_count := 0
var _key_log := ""

# 移动
var _move_speed := 4.0
var _move_dir := Vector3.ZERO

# 跳跃
var _velocity_y := 0.0
var _gravity := 12.0
var _jump_speed := 6.0
var _is_grounded := true
var _base_y := 0.5  # Cube 半高

# 点击移动（平滑滑动到目标）
var _target_pos: Vector3 = Vector3.ZERO
var _is_moving_to_target := false

# 拖拽
var _is_dragging := false
var _drag_offset := Vector3.ZERO

# --- 节点引用 ---
@onready var status_label: Label = get_node_or_null("UI/Panel/VBox/StatusLabel")
@onready var click_count_label: Label = get_node_or_null("UI/Panel/VBox/ClickCountLabel")
@onready var touch_count_label: Label = get_node_or_null("UI/Panel/VBox/TouchCountLabel")
@onready var key_log_label: Label = get_node_or_null("UI/Panel/VBox/KeyLogLabel")
@onready var color_rect: ColorRect = get_node_or_null("UI/ColorRect")
@onready var cube: MeshInstance3D = get_node_or_null("Cube")
@onready var camera: Camera3D = get_node_or_null("Camera3D")
@onready var ground: MeshInstance3D = get_node_or_null("Ground")
@onready var audio_player: AudioStreamPlayer = get_node_or_null("AudioPlayer")

# ============================================================
#  生命周期
# ============================================================

func _ready() -> void:
	_update_status("等待连接...")
	connect_stream_server.call_deferred()

func _process(delta: float) -> void:
	if not cube:
		return

	# --- WASD 持续移动 ---
	_move_dir = Vector3.ZERO
	if Input.is_key_pressed(KEY_W): _move_dir.z -= 1
	if Input.is_key_pressed(KEY_S): _move_dir.z += 1
	if Input.is_key_pressed(KEY_A): _move_dir.x -= 1
	if Input.is_key_pressed(KEY_D): _move_dir.x += 1

	if _move_dir != Vector3.ZERO:
		_move_dir = _move_dir.normalized()
		cube.position.x += _move_dir.x * _move_speed * delta
		cube.position.z += _move_dir.z * _move_speed * delta
		_is_moving_to_target = false
	if not cube.position.is_finite():
		cube.position = Vector3(0, _base_y, 0)
		# 限制范围
		cube.position.x = clampf(cube.position.x, -14, 14)
		cube.position.z = clampf(cube.position.z, -14, 14)

	# --- 点击目标平滑移动 ---
	if _is_moving_to_target:
		var diff := _target_pos - Vector3(cube.position.x, 0, cube.position.z)
		var dist := diff.length()
		if dist < 0.05:
			_is_moving_to_target = false
		else:
			var step := minf(_move_speed * delta, dist)
			var dir := diff.normalized()
			cube.position.x += dir.x * step
			cube.position.z += dir.z * step

	# --- 跳跃 / 重力 ---
	if _is_grounded and Input.is_key_pressed(KEY_SPACE):
		_velocity_y = _jump_speed
		_is_grounded = false

	if not _is_grounded:
		_velocity_y -= _gravity * delta
		cube.position.y += _velocity_y * delta
		if cube.position.y <= _base_y:
			cube.position.y = _base_y
			_velocity_y = 0.0
			_is_grounded = true

# ============================================================
#  输入事件
# ============================================================

func _unhandled_input(event: InputEvent) -> void:
	# --- 鼠标左键点击 / 拖拽 ---
	if event is InputEventMouseButton and event.button_index == MOUSE_BUTTON_LEFT:
		if event.pressed:
			_click_count += 1
			if click_count_label:
				click_count_label.text = "鼠标点击: %d" % _click_count
			_flash_color(Color.CYAN)

			# 射线检测：优先拖拽 Cube，否则移动到地面目标
			var hit := _raycast(event.position)
			if hit and _is_on_cube(hit):
				_is_dragging = true
				_drag_offset = cube.position - hit
				_drag_offset.y = 0
			elif hit:
				_target_pos = Vector3(hit.x, 0, hit.z)
				_is_moving_to_target = true
		else:
			_is_dragging = false

	# --- 鼠标拖拽移动 ---
	if event is InputEventMouseMotion and _is_dragging and cube:
		var hit := _raycast(event.position)
		if hit:
			var new_pos := hit + _drag_offset
			cube.position.x = clampf(new_pos.x, -14, 14)
			cube.position.z = clampf(new_pos.z, -14, 14)
			_is_moving_to_target = false

	# --- 触摸 ---
	if event is InputEventScreenTouch:
		if event.pressed:
			_touch_count += 1
			if touch_count_label:
				touch_count_label.text = "触摸点击: %d" % _touch_count
			_flash_color(Color.MAGENTA)

			var hit := _raycast(event.position)
			if hit:
				_target_pos = Vector3(hit.x, 0, hit.z)
				_is_moving_to_target = true

	# --- 键盘（日志） ---
	if event is InputEventKey and event.pressed and not event.echo:
		var key_name = keycode_to_string(event.keycode)
		_key_log = key_name + " | " + _key_log
		if _key_log.length() > 60:
			_key_log = _key_log.substr(0, 60)
		if key_log_label:
			key_log_label.text = "键盘: %s" % _key_log
		if not (event.keycode in [KEY_W, KEY_S, KEY_A, KEY_D, KEY_SPACE]):
			_flash_color(Color.GREEN_YELLOW)

# ============================================================
#  射线检测（相机 → 地面 y=0 平面）
# ============================================================

func _raycast(screen_pos: Vector2) -> Vector3:
	if not camera:
		return Vector3.ZERO
	var from := camera.project_ray_origin(screen_pos)
	var dir := camera.project_ray_normal(screen_pos)
	# 防护：远程输入坐标可能超出视口，投影结果为 NaN/Inf
	if not (from.is_finite() and dir.is_finite()):
		return Vector3.ZERO
	# 与 y=0 平面求交
	if absf(dir.y) < 0.001:
		return Vector3.ZERO
	var t := -from.y / dir.y
	if t < 0:
		return Vector3.ZERO
	var hit := from + dir * t
	if not hit.is_finite():
		return Vector3.ZERO
	return hit

func _is_on_cube(point: Vector3) -> bool:
	if not cube:
		return false
	var diff := point - cube.position
	return absf(diff.x) < 0.6 and absf(diff.z) < 0.6 and diff.y < 1.2

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

# ============================================================
#  信令连接 (GDExtension snake_case signals)
# ============================================================

func connect_stream_server() -> void:
	var ss = get_node_or_null("StreamServer")
	if ss and ss.has_signal("client_connected"):
		ss.connect("client_connected", _on_client_connected)
		ss.connect("client_disconnected", _on_client_disconnected)
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

func _on_red_pressed() -> void:
	if cube: cube.rotation_degrees.y += 45
	_flash_color(Color.RED)

func _on_green_pressed() -> void:
	if cube: cube.rotation_degrees.x += 45
	_flash_color(Color.GREEN)

func _on_blue_pressed() -> void:
	if cube: cube.rotation_degrees.z += 45
	_flash_color(Color.BLUE)

func _on_reset_pressed() -> void:
	_click_count = 0
	_touch_count = 0
	_key_log = ""
	if click_count_label: click_count_label.text = "鼠标点击: 0"
	if touch_count_label: touch_count_label.text = "触摸点击: 0"
	if key_log_label: key_log_label.text = "键盘: "
	if cube:
		cube.position = Vector3(0, _base_y, 0)
		cube.rotation_degrees = Vector3.ZERO
	_is_moving_to_target = false
	_is_dragging = false
	_velocity_y = 0.0
	_is_grounded = true

func _on_audio_pressed() -> void:
	if audio_player:
		if audio_player.playing:
			audio_player.stop()
		else:
			var audio_path := "res://audio/Calm Inspiring Piano Logo.mp3"
			if ResourceLoader.exists(audio_path):
				var stream = load(audio_path)
				if stream:
					audio_player.stream = stream
					audio_player.play()
					_flash_color(Color.MEDIUM_PURPLE)
				else:
					push_warning("无法加载音频文件")
			else:
				push_warning("音频文件不存在: %s" % audio_path)

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
