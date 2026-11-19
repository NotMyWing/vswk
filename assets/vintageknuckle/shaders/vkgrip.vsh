#version 330 core

layout(location = 0) in vec3 vertexPosition;
layout(location = 1) in vec2 uvIn;
layout(location = 2) in vec4 colorIn;
layout(location = 3) in int flags;

uniform vec4 anchor;
uniform mat4 relativeClip;
uniform mat4 restClip;
uniform float handReturn;
uniform vec3 handSize;
uniform float fromFingertips;
uniform float palmRoll;
uniform vec3 across;
uniform vec3 up;
uniform vec3 outward;
uniform float modelSize;

out vec2 uv;
out vec4 tint;
out float wristY;
out vec3 worldNormal;

#include vertexflagbits.ash

void main() {
	vec3 point = vertexPosition;
	vec3 normal = unpackNormal(flags);

	if (normal.y > 0.95) point.y = min(point.y, handSize.y);
	wristY = point.y;

	vec3 center = handSize * 0.5;
	vec3 offset = across * ((point.x - center.x) * palmRoll)
		+ outward * (point.y - fromFingertips)
		+ up * (center.z - (point.z - center.z) * palmRoll);
	gl_Position = anchor + relativeClip * vec4(offset * modelSize, 0);

	if (handReturn > 0) {
		vec4 rest = restClip * vec4(point, 1);
		// tuck the hand offscreen if the model has no idle hand
		if (rest == vec4(0)) rest = vec4(palmRoll * 0.55, -1.1, 0, 1);
		gl_Position = mix(gl_Position, rest, handReturn);
	}

	worldNormal = (across * normal.x - up * normal.z) * palmRoll + outward * normal.y;
	uv = uvIn;
	tint = colorIn;
}
