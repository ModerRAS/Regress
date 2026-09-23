shader_type spatial;
render_mode unshaded, cull_back;

// The placement preview: the chunk tiles, translucent and dimmed. Deliberately NO
// ALPHA_SCISSOR_THRESHOLD - writing it puts the material on the opaque alpha-tested pipeline,
// which ignores ALPHA entirely (verified on this build: the ghost rendered solid with it).
// Chunks keep res://src/voxel_tiles.gdshader, byte-identical to master.
uniform sampler2DArray tiles : source_color, filter_nearest, repeat_disable;

varying float v_tile;

void vertex() {
	v_tile = UV2.x;
}

void fragment() {
	vec4 t = texture(tiles, vec3(UV, v_tile));
	ALBEDO = t.rgb * COLOR.rgb * 0.75;
	ALPHA = t.a * 0.5;
}
