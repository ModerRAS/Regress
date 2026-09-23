shader_type spatial;
render_mode unshaded, cull_back, depth_draw_opaque;

// One sampler per size class (design P2.3): UV2 = (layer, class), UV is tile-local, COLOR is
// tint only. GLSL cannot index sampler arrays dynamically, so the class picks the sampler
// through a uniform branch. See docs/texture-packs.md.
uniform sampler2DArray tiles0 : source_color, filter_nearest, repeat_disable;
uniform sampler2DArray tiles1 : source_color, filter_nearest, repeat_disable;
uniform sampler2DArray tiles2 : source_color, filter_nearest, repeat_disable;
uniform sampler2DArray tiles3 : source_color, filter_nearest, repeat_disable;

varying float v_tile;
varying float v_class;

void vertex() {
	v_tile = UV2.x;
	v_class = UV2.y;
}

void fragment() {
	vec4 t = v_class < 0.5 ? texture(tiles0, vec3(UV, v_tile))
		: v_class < 1.5 ? texture(tiles1, vec3(UV, v_tile))
		: v_class < 2.5 ? texture(tiles2, vec3(UV, v_tile))
		: texture(tiles3, vec3(UV, v_tile));
	ALBEDO = t.rgb * COLOR.rgb;
	ALPHA = t.a;
	ALPHA_SCISSOR_THRESHOLD = 0.5;
}
