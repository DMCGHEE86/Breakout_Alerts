using Futu.OpenApi;
using Futu.OpenApi.Pb;

namespace BreakoutAlerts.App.Services.Moomoo;

/// <summary>
/// The parts of <c>FTSPI_Trd</c> this application does not use.
/// </summary>
/// <remarks>
/// <c>FTSPI_Trd</c> has no default implementations, so every member must be present even
/// though only a handful carry replies we wait on. These are generated from the compiler's
/// own CS0535 list rather than transcribed by hand - regenerate the same way if futu-api is
/// upgraded.
///
/// <para>Kept in a separate file so the real logic in <see cref="MoomooTradeConnection"/>
/// stays readable. Anything moved out of here and implemented for real must be deleted from
/// this file, or the partial class will not compile.</para>
/// </remarks>
public sealed partial class MoomooTradeConnection
{
    /// <inheritdoc />
    public void OnReply_GetMaxTrdQtys(FTAPI_Conn client, uint nSerialNo, TrdGetMaxTrdQtys.Response rsp)
    {
    }

    /// <inheritdoc />
    public void OnReply_GetComboMaxTrdQtys(FTAPI_Conn client, uint nSerialNo, TrdGetComboMaxTrdQtys.Response rsp)
    {
    }

    /// <inheritdoc />
    public void OnReply_GetOrderFillList(FTAPI_Conn client, uint nSerialNo, TrdGetOrderFillList.Response rsp)
    {
    }

    /// <inheritdoc />
    public void OnReply_GetHistoryOrderList(FTAPI_Conn client, uint nSerialNo, TrdGetHistoryOrderList.Response rsp)
    {
    }

    /// <inheritdoc />
    public void OnReply_GetHistoryOrderFillList(FTAPI_Conn client, uint nSerialNo, TrdGetHistoryOrderFillList.Response rsp)
    {
    }

    /// <inheritdoc />
    public void OnReply_GetMarginRatio(FTAPI_Conn client, uint nSerialNo, TrdGetMarginRatio.Response rsp)
    {
    }

    /// <inheritdoc />
    public void OnReply_GetOrderFee(FTAPI_Conn client, uint nSerialNo, TrdGetOrderFee.Response rsp)
    {
    }

    /// <inheritdoc />
    public void OnReply_GetFlowSummary(FTAPI_Conn client, uint nSerialNo, TrdFlowSummary.Response rsp)
    {
    }

    /// <inheritdoc />
    public void OnReply_PlaceComboOrder(FTAPI_Conn client, uint nSerialNo, TrdPlaceComboOrder.Response rsp)
    {
    }

    /// <inheritdoc />
    /// <remarks>
    /// Unused because the GUI build of OpenD refuses API unlocks outright - measured
    /// 2026-09-04, "The GUI version of OpenD has disabled the unlock interface." Trading is
    /// unlocked in OpenD's own window instead. See plan.md section 4t.
    /// </remarks>
    public void OnReply_UnlockTrade(FTAPI_Conn client, uint nSerialNo, TrdUnlockTrade.Response rsp)
    {
    }
}
