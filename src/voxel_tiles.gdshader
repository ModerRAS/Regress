shader_type spatial;
render_mode unshaded, cull_back, depth_draw_opaque;

// Twelve pack tiles, one layer per frozen key index. UV2.x selects the layer, UV is
// tile-local, COLOR is tint only. See docs/texture-packs.md.
uniform sampler2DArray tiles : source_color, filter_nearest, repeat_disable;

// The chunk path keeps the defaults (1.0), so its output is unchanged. The placement ghost
// duplicates the material with alpha_scale=0.5 and albedo_scale=0.75 (design: translucent +
// dimmed, but read against the real tile texture so the rotation is readable face by face).
uniform float alpha_scale = 1.0;
uniform float albedo_scale = 1.0;

varying float v_tile;

void vertex() {
	v_tile = UV2.x;
}

void fragment() {
	vec4 t = texture(tiles, vec3(UV, v_tile));
	ALBEDO = t.rgb * COLOR.rgb * albedo_scale;
	ALPHA = t.a * alpha_scale;
	// Below 1.0 the ghost must not scissor its own translucent pixels away.
	ALPHA_SCISSOR_THRESHOLD = alpha_scale < 1.0 ? 0.0 : 0.5;
}
