from enum import IntFlag

import comtypes.gen._00020430_0000_0000_C000_000000000046_0_2_0 as __wrapper_module__
from comtypes.gen._00020430_0000_0000_C000_000000000046_0_2_0 import (
    IFontEventsDisp, DISPPARAMS, OLE_YSIZE_HIMETRIC, Picture,
    OLE_ENABLEDEFAULTBOOL, OLE_COLOR, Color, OLE_YPOS_PIXELS,
    typelib_path, COMMETHOD, IFont, Font, IDispatch, CoClass,
    FONTUNDERSCORE, OLE_XPOS_CONTAINER, FONTNAME, FONTBOLD, VgaColor,
    OLE_YPOS_HIMETRIC, GUID, _check_version, OLE_YSIZE_PIXELS,
    OLE_HANDLE, Gray, Checked, OLE_XSIZE_CONTAINER, StdFont,
    OLE_CANCELBOOL, OLE_XSIZE_HIMETRIC, OLE_OPTEXCLUSIVE,
    OLE_XSIZE_PIXELS, FONTSTRIKETHROUGH, OLE_XPOS_HIMETRIC,
    OLE_XPOS_PIXELS, OLE_YSIZE_CONTAINER, OLE_YPOS_CONTAINER, dispid,
    Monochrome, Unchecked, FONTITALIC, DISPMETHOD, EXCEPINFO,
    DISPPROPERTY, IUnknown, IFontDisp, IPictureDisp, StdPicture,
    HRESULT, Default, FontEvents, FONTSIZE, _lcid, BSTR, Library,
    IEnumVARIANT, VARIANT_BOOL, IPicture
)


class OLE_TRISTATE(IntFlag):
    Unchecked = 0
    Checked = 1
    Gray = 2


class LoadPictureConstants(IntFlag):
    Default = 0
    Monochrome = 1
    VgaColor = 2
    Color = 4


__all__ = [
    'IFontEventsDisp', 'OLE_YSIZE_HIMETRIC', 'OLE_TRISTATE',
    'Picture', 'OLE_YPOS_CONTAINER', 'OLE_ENABLEDEFAULTBOOL',
    'OLE_COLOR', 'Color', 'OLE_YPOS_PIXELS', 'typelib_path', 'IFont',
    'Monochrome', 'Unchecked', 'FONTITALIC', 'Font', 'FONTUNDERSCORE',
    'OLE_XPOS_CONTAINER', 'FONTNAME', 'IFontDisp', 'FONTBOLD',
    'VgaColor', 'OLE_YPOS_HIMETRIC', 'IPictureDisp', 'StdPicture',
    'OLE_YSIZE_PIXELS', 'OLE_HANDLE', 'Gray', 'Default', 'Checked',
    'FontEvents', 'OLE_XSIZE_CONTAINER', 'LoadPictureConstants',
    'OLE_CANCELBOOL', 'OLE_XSIZE_HIMETRIC', 'FONTSIZE', 'StdFont',
    'OLE_XSIZE_PIXELS', 'FONTSTRIKETHROUGH', 'OLE_OPTEXCLUSIVE',
    'OLE_XPOS_HIMETRIC', 'OLE_XPOS_PIXELS', 'Library',
    'OLE_YSIZE_CONTAINER', 'IPicture'
]

