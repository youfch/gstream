extends Node3D

## gStream Multiplay Demo — 多人连接测试
## 多个浏览器客户端同时连接，共享同一场景
## 验证：MultiStreamServer 多连接管理 + 独立输入 + multiplay 广播

# --- 状态变量 ---
var _clients := {}  # connection_id -> label
var _click_count := 0
var _key_log := ""

# --- 节点引用 ---
@onready var status_label: Label = get_node_or_null("UI/Panel/VBox/StatusLabel")
@onready var client_count_label: Label = get_node_or_null("UI/Panel/VBox/ClientCountLabel")
@onready var click_count_label: Label = get_node_or_null("UI/Panel/VBox/ClickCountLabel")
@onready var key_log_label: Label = get_node_or_null("UI/Panel/VBox/KeyLogLabel")
@onready var color_rect: ColorRect = get_node_or_null("UI/ColorRect")
@onready var cube: MeshInstance3D = get_node_or_null("Cube")
@onready var client_list: VBoxContainer = get_node_or_null("UI/ClientPanel/VBox/ScrollContainer/ClientList")
@onready var audio_player: AudioStreamPlayer = get_node_or_null("AudioPlayer")

# ============================================================
#  生命周期
# ============================================================

func _ready() -> void:
	_update_status("等待连接...")
	connect_stream_server.call_deferred()

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

func _update_client_list() -> void:
	if not client_list:
		return
	# Clear existing children
	for child in client_list.get_children():
		child.queue_free()
	# Add labels for each client
	for conn_id in _clients:
		var label = Label.new()
		label.text = "● %s (%s)" % [_clients[conn_id], conn_id.left(8)]
		client_list.add_child(label)
	# Update count label
	if client_count_label:
		client_count_label.text = "客户端: %d" % _clients.size()

# ============================================================
#  信令连接
# ============================================================

func connect_stream_server() -> void:
	var ss = get_node_or_null("MultiStreamServer")
	if ss and ss.has_signal("ClientConnected"):
		ss.connect("ClientConnected", _on_client_connected)
		ss.connect("ClientDisconnected", _on_client_disconnected)
		ss.connect("MultiplayMessageReceived", _on_multiplay_message)
		_update_status("就绪，等待连接...")
	elif ss:
		connect_stream_server.call_deferred()

func _on_client_connected(connection_id: String, label: String) -> void:
	_clients[connection_id] = label
	_update_client_list()
	_update_status("客户端连接: %s (%s) — 共 %d" % [label, connection_id, _clients.size()])
	_flash_color(Color.CYAN)

func _on_client_disconnected(connection_id: String) -> void:
	_clients.erase(connection_id)
	_update_client_list()
	_update_status("客户端断开: %s — 剩余 %d" % [connection_id, _clients.size()])
	_flash_color(Color.ORANGE)

func _on_multiplay_message(connection_id: String, message: String) -> void:
	# message is JSON: { "type": 0, "argument": "randomNumber" }
	_update_status("Multiplay [%s]: %s" % [connection_id.left(8), message])
	_flash_color(Color.MEDIUM_PURPLE)

func _unhandled_input(event: InputEvent) -> void:
	# --- 鼠标左键点击 ---
	if event is InputEventMouseButton and event.button_index == MOUSE_BUTTON_LEFT:
		if event.pressed:
			_click_count += 1
			if click_count_label:
				click_count_label.text = "点击: %d" % _click_count
			if cube:
				cube.rotation_degrees.y += 15
			_flash_color(Color.CYAN)

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
#  UI 按钮回调
# ============================================================

func _on_reset_pressed() -> void:
	_click_count = 0
	_key_log = ""
	if click_count_label: click_count_label.text = "点击: 0"
	if key_log_label: key_log_label.text = "键盘: "
	if cube:
		cube.position = Vector3(0, 0.5, 0)
		cube.rotation_degrees = Vector3.ZERO

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
