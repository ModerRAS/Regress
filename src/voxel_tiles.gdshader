shader_type spatial;
render_mode unshaded, cull_back, depth_draw_opaque;

// Twelve pack tiles, one layer per frozen key index. UV2.x selects the layer, UV is
// tile-local, COLOR is tint only. See docs/texture-packs.md.
uniform sampler2DArray tiles : source_color, filter_nearest, repeat_disable;

varying float v_tile;

void vertex() {
	v_tile = UV2.x;
}

void fragment() {
	vec4 t = texture(tiles, vec3(UV, v_tile));
	ALBEDO = t.rgb * COLOR.rgb;
	ALPHA = t.a;
	ALPHA_SCISSOR_THRESHOLD = 0.5;
}
