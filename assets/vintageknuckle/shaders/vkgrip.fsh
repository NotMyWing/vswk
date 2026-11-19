#version 330 core

uniform sampler2D entityTex;
uniform vec4 light;
uniform vec3 ambient;
uniform vec3 handSize;
uniform float strain;

in vec2 uv;
in vec4 tint;
in float wristY;
in vec3 worldNormal;

layout(location = 0) out vec4 outColor;
layout(location = 1) out vec4 outGlow;

void main() {
	if (wristY > handSize.y + 0.0001) discard;

	vec4 skin = texture(entityTex, uv) * tint;
	if (skin.a < 0.05) discard;

	skin.rgb = mix(skin.rgb, vec3(max(skin.r, 0.65), skin.g * 0.17, skin.b * 0.17), strain);
	float shade = 0.8 + 0.2 * normalize(worldNormal).y;
	outColor = vec4(skin.rgb * max(vec3(0.03), light.rgb + light.a * ambient) * shade, skin.a);
	outGlow = vec4(0, 0, 0, 1);
}
