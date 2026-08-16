//////////////////////////////////////////
//
// NOTE: This is *not* a valid shader file
//
///////////////////////////////////////////
Shader "UI/Default Font" {
Properties {

_MainTex ("Font Texture", 2D) = "white" { }

_Color ("Tint", Color) = (1,1,1,1)

_StencilComp ("Stencil Comparison", Float) = 8.0

_Stencil ("Stencil ID", Float) = 0.0

_StencilOp ("Stencil Operation", Float) = 0.0

_StencilWriteMask ("Stencil Write Mask", Float) = 255.0

_StencilReadMask ("Stencil Read Mask", Float) = 255.0

_ColorMask ("Color Mask", Float) = 15.0

[Toggle(UNITY_UI_ALPHACLIP)] _UseUIAlphaClip ("Use Alpha Clip", Float) = 0.0

}
Fall back "UI/Default"
}