extends Node

## gStream GDE Demo — GDScript test script
## Creates a StreamServer node via GDExtension and configures it for streaming.

func _ready() -> void:
	print("[GDE Demo] Creating StreamServer via GDExtension...")

	# Create StreamServer node (registered via GodotRegistry in C#)
	var stream_server = StreamServer.new()

	# Assign the SubViewport as capture source
	var viewport = $SubViewport
	stream_server.source_viewport = viewport
	stream_server.capture_main_window = false

	# Configure encoding
	stream_server.target_fps = 30
	stream_server.bitrate_kbps = 4000
	stream_server.codec = 0  # Auto

	# Configure signaling
	stream_server.signaling_url = "ws://localhost:80"

	# Connect signals
	stream_server.stream_started.connect(_on_stream_started)
	stream_server.stream_stopped.connect(_on_stream_stopped)
	stream_server.client_connected.connect(_on_client_connected)
	stream_server.stats_updated.connect(_on_stats_updated)

	# Add as child — StreamServer._Ready() auto-starts
	add_child(stream_server)

	print("[GDE Demo] StreamServer added to scene tree")


func _on_stream_started(width: int, height: int) -> void:
	print("[GDE Demo] Stream started: %dx%d" % [width, height])


func _on_stream_stopped() -> void:
	print("[GDE Demo] Stream stopped")


func _on_client_connected(connection_id: String) -> void:
	print("[GDE Demo] Client connected: %s" % connection_id)


func _on_stats_updated(fps: int, bitrate_kbps: int, pending: int, encode_ms: float, capture_ms: float) -> void:
	print("[GDE Demo] Stats: %d fps, %d kbps, encode=%.1fms, capture=%.1fms" % [fps, bitrate_kbps, encode_ms, capture_ms])
