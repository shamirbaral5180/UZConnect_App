package com.uzconnect.agent;

import android.os.IBinder;

import java.lang.reflect.Method;

/**
 * Root-side USSD submitter. ADB on the tested stock image runs as uid 0, so this
 * class can call the hidden ITelephony.handlePinMmi method that a normal app
 * cannot reach because it requires the signature permission MODIFY_PHONE_STATE.
 *
 * Usage:
 *   CLASSPATH=/data/local/tmp/uzconnect-agent.apk app_process /system/bin \
 *       com.uzconnect.agent.UssdCli '*101#'
 */
public final class UssdCli {

    private UssdCli() {
    }

    public static void main(String[] args) {
        if (args.length == 0 || args[0] == null || args[0].length() == 0) {
            System.out.println("ERROR=missing USSD code");
            return;
        }
        System.out.println("CODE=" + args[0] + " LENGTH=" + args[0].length());

        try {
            Class<?> serviceManager = Class.forName("android.os.ServiceManager");
            IBinder binder = (IBinder) serviceManager
                    .getMethod("getService", String.class)
                    .invoke(null, "phone");
            if (binder == null) {
                System.out.println("ERROR=phone service unavailable");
                return;
            }

            Class<?> stub = Class.forName("com.android.internal.telephony.ITelephony$Stub");
            Object telephony = stub.getMethod("asInterface", IBinder.class).invoke(null, binder);
            for (Method candidate : telephony.getClass().getMethods()) {
                String name = candidate.getName().toLowerCase();
                if (name.contains("pin") || name.contains("ussd") || name.contains("mmi")) {
                    System.out.println("METHOD=" + candidate);
                }
            }
            Method handle = telephony.getClass().getMethod("handlePinMmi", String.class);
            Object result = handle.invoke(telephony, args[0]);
            System.out.println("SERVICE_RESULT=" + result);

            // Diagnose the phone object that the service delegates to.
            Class<?> phoneFactory = Class.forName("com.android.internal.telephony.PhoneFactory");
            Object phone = phoneFactory.getMethod("getDefaultPhone").invoke(null);
            System.out.println("PHONE=" + (phone == null ? "null" : phone.getClass().getName()));
            if (phone != null) {
                Method direct = phone.getClass().getMethod("handlePinMmi", String.class);
                Object directResult = direct.invoke(phone, args[0]);
                System.out.println("DIRECT_RESULT=" + directResult);
            }
        } catch (Throwable throwable) {
            Throwable cause = throwable;
            while (cause instanceof java.lang.reflect.InvocationTargetException
                    && cause.getCause() != null) {
                cause = cause.getCause();
            }
            System.out.println("ERROR=" + cause.getClass().getName() + ": " + cause.getMessage());
        }
    }
}
