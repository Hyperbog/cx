using System;

namespace CX.Services.Camera;

/// <summary>Tracks eye height above the actor through a first-person jump.</summary>
internal struct JumpEyeHeight
{
    private const float LandingBlendSeconds = 0.22f;

    private bool _hasHeight, _wasAirborne;
    private float _height, _landingStart, _landingRemaining;

    internal void Reset() => this = default;

    internal float Resolve(float actorZ, float liveEyeZ, bool airborne, float dt)
    {
        float liveHeight = liveEyeZ - actorZ;
        if (!float.IsFinite(liveHeight))
        {
            Reset();
            return liveEyeZ;
        }

        if (!_hasHeight)
        {
            _height = liveHeight;
            _hasHeight = true;
        }

        // Airborne translation comes from the actor. Walking pose can continue
        // on the rig in flight, including a dip that would hide the launch.
        if (airborne)
        {
            _wasAirborne = true;
            _landingRemaining = 0f;
            return actorZ + _height;
        }

        if (_wasAirborne)
        {
            _wasAirborne = false;
            _landingStart = _height;
            _landingRemaining = LandingBlendSeconds;
            return actorZ + _height;
        }

        if (_landingRemaining > 0f)
        {
            if (float.IsFinite(dt) && dt > 0f)
                _landingRemaining = MathF.Max(0f, _landingRemaining - dt);
            if (_landingRemaining > 0f)
            {
                float t = 1f - _landingRemaining / LandingBlendSeconds;
                float blend = t * t * (3f - 2f * t);
                _height = _landingStart + (liveHeight - _landingStart) * blend;
                return actorZ + _height;
            }
        }

        _height = liveHeight;
        return liveEyeZ;
    }
}
