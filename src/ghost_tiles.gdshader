shader_type spatial;
render_mode unshaded, cull_back;

// The placement preview: the chunk tiles, translucent and dimmed. Deliberately NO
// ALPHA_SCISSOR_THRESHOLD - writing it puts the material on the opaque alpha-tested pipeline,
// which ignores ALPHA entirely (verified on this build: the ghost rendered solid with it).
// One sampler per size class, selected exactly like voxel_tiles.gdshader (design P2.3).
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
	ALBEDO = t.rgb * COLOR.rgb * 0.75;
	ALPHA = t.a * 0.5;
}
